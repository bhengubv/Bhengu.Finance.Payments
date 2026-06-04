// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.BricsPay.Configuration;
using Bhengu.Finance.Payments.BricsPay.Currency;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.BricsPay.Providers;

/// <summary>
/// BRICS Pay cross-border payments. Supports payments and payouts within BRICS nations
/// (South Africa, Brazil, Russia, India, China) with automatic currency conversion. Honours
/// per-call <c>IdempotencyKey</c> via the shared <see cref="IBhenguDistributedCache"/> for 24
/// hours.
/// </summary>
public sealed class BricsPayPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly BricsPayOptions _options;
    private readonly ICurrencyExchangeService _exchangeService;
    private readonly ILogger<BricsPayPaymentProvider> _logger;
    private readonly IBhenguDistributedCache _cache;
    private readonly string _baseUrl;
    private static readonly TimeSpan s_idempotencyTtl = TimeSpan.FromHours(24);

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.BricsPay;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.CrossBorder |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public BricsPayPaymentProvider(
        HttpClient httpClient,
        IOptions<BricsPayOptions> options,
        ICurrencyExchangeService exchangeService,
        ILogger<BricsPayPaymentProvider> logger,
        IBhenguDistributedCache? cache = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _exchangeService = exchangeService ?? throw new ArgumentNullException(nameof(exchangeService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cache = cache ?? new InMemoryBhenguDistributedCache();

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(BricsPayOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(BricsPayOptions.SecretKey)} is required");

        _baseUrl = _options.UseSandbox
            ? (_options.SandboxUrl ?? "https://sandbox.bricspay.org/api/v1")
            : (_options.BaseUrl ?? "https://api.bricspay.org/api/v1");
    }

    /// <inheritdoc/>
    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = BhenguPaymentDiagnostics.StartChargeActivity(ProviderName, request.Currency);
        var startedAt = DateTime.UtcNow;
        var outcomeTag = BhenguPaymentDiagnostics.Outcomes.Error;
        try
        {
            var cached = await TryGetCachedAsync<PaymentResponse>(request.IdempotencyKey, "charge", ct).ConfigureAwait(false);
            if (cached is not null)
            {
                outcomeTag = BhenguPaymentDiagnostics.Outcomes.Success;
                return cached;
            }

            var sourceCurrency = ParseCurrency(request.Currency);
            var targetCurrency = request.Metadata?.TryGetValue("target_currency", out var tc) == true
                ? ParseCurrency(tc)
                : sourceCurrency;

            ConversionResult? conversion = null;
            if (sourceCurrency != targetCurrency)
            {
                conversion = await _exchangeService.LockRateAsync(request.Amount, sourceCurrency, targetCurrency, ct: ct).ConfigureAwait(false);
                _logger.LogInformation("Currency conversion {Amount} {From} -> {Final} {To} @ {Rate}",
                    request.Amount, sourceCurrency, conversion.FinalAmount, targetCurrency, conversion.ExchangeRate);
            }

            var transactionId = request.IdempotencyKey ?? GenerateTransactionId();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var requestBody = new
            {
                merchant_id = _options.MerchantId,
                transaction_id = transactionId,
                payment_method_token = request.PaymentMethodToken,
                amount = conversion?.FinalAmount ?? request.Amount,
                currency = targetCurrency.ToString(),
                source_currency = sourceCurrency.ToString(),
                source_amount = request.Amount,
                exchange_rate = conversion?.ExchangeRate ?? 1m,
                quote_id = conversion?.QuoteId,
                description = request.Description,
                metadata = request.Metadata,
                timestamp
            };

            var response = await SendSignedRequestAsync($"{_baseUrl}/payments", requestBody, timestamp, request.IdempotencyKey, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                outcomeTag = BhenguPaymentDiagnostics.Outcomes.RateLimited;
                var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
                throw new ProviderRateLimitException(ProviderName, retryAfter, body);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("BRICS Pay payment failed: {StatusCode} {Body}", response.StatusCode, body);
                if ((int)response.StatusCode is >= 400 and < 500)
                {
                    outcomeTag = BhenguPaymentDiagnostics.Outcomes.Declined;
                    throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), body);
                }
                outcomeTag = BhenguPaymentDiagnostics.Outcomes.Unavailable;
                throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {body}");
            }

            var result = JsonSerializer.Deserialize<BricsPayApiResponse>(body);
            var status = MapStatus(result?.Status ?? "pending");
            var pr = new PaymentResponse
            {
                GatewayReference = result?.PaymentId ?? transactionId,
                Status = status,
                Amount = conversion?.FinalAmount ?? request.Amount,
                Currency = targetCurrency.ToString(),
                ProcessedAt = DateTime.UtcNow,
                Message = result?.Message
            };

            outcomeTag = status switch
            {
                PaymentStatus.Completed => BhenguPaymentDiagnostics.Outcomes.Success,
                PaymentStatus.Pending => BhenguPaymentDiagnostics.Outcomes.Pending,
                PaymentStatus.Failed => BhenguPaymentDiagnostics.Outcomes.Declined,
                _ => BhenguPaymentDiagnostics.Outcomes.Pending
            };
            await TrySetCachedAsync(request.IdempotencyKey, "charge", pr, ct).ConfigureAwait(false);
            return pr;
        }
        catch (PaymentDeclinedException) { outcomeTag = BhenguPaymentDiagnostics.Outcomes.Declined; throw; }
        catch (ProviderRateLimitException) { outcomeTag = BhenguPaymentDiagnostics.Outcomes.RateLimited; throw; }
        catch (ProviderUnavailableException) { outcomeTag = BhenguPaymentDiagnostics.Outcomes.Unavailable; throw; }
        finally
        {
            activity.SetOutcome(outcomeTag);
            BhenguPaymentDiagnostics.ChargesTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", outcomeTag));
            BhenguPaymentDiagnostics.ChargeDurationMs.Record(
                (DateTime.UtcNow - startedAt).TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", outcomeTag));
        }
    }

    /// <inheritdoc/>
    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = BhenguPaymentDiagnostics.StartRefundActivity(ProviderName, request.GatewayReference);
        var outcomeTag = BhenguPaymentDiagnostics.Outcomes.Error;
        try
        {
            var cached = await TryGetCachedAsync<RefundResponse>(request.IdempotencyKey, "refund", ct).ConfigureAwait(false);
            if (cached is not null)
            {
                outcomeTag = BhenguPaymentDiagnostics.Outcomes.Success;
                return cached;
            }

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var requestBody = new
            {
                merchant_id = _options.MerchantId,
                original_payment_id = request.GatewayReference,
                refund_amount = request.Amount,
                reason = request.Reason,
                timestamp
            };

            var response = await SendSignedRequestAsync($"{_baseUrl}/refunds", requestBody, timestamp, request.IdempotencyKey, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                outcomeTag = BhenguPaymentDiagnostics.Outcomes.RateLimited;
                var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
                throw new ProviderRateLimitException(ProviderName, retryAfter, body);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("BRICS Pay refund failed: {StatusCode} {Body}", response.StatusCode, body);
                outcomeTag = BhenguPaymentDiagnostics.Outcomes.Declined;
                throw new BhenguPaymentException(
                    ProviderName,
                    $"BRICS Pay refund failed: HTTP {(int)response.StatusCode}",
                    providerErrorCode: ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                    providerErrorMessage: body);
            }

            var result = JsonSerializer.Deserialize<BricsPayApiResponse>(body);
            var status = MapStatus(result?.Status ?? "pending");
            var rr = new RefundResponse
            {
                GatewayReference = result?.PaymentId ?? $"REFUND_{Guid.NewGuid():N}",
                Amount = request.Amount,
                Status = status,
                ProcessedAt = DateTime.UtcNow,
                Message = result?.Message
            };

            outcomeTag = status == PaymentStatus.Failed
                ? BhenguPaymentDiagnostics.Outcomes.Declined
                : BhenguPaymentDiagnostics.Outcomes.Success;
            await TrySetCachedAsync(request.IdempotencyKey, "refund", rr, ct).ConfigureAwait(false);
            return rr;
        }
        catch (PaymentDeclinedException) { outcomeTag = BhenguPaymentDiagnostics.Outcomes.Declined; throw; }
        catch (ProviderRateLimitException) { outcomeTag = BhenguPaymentDiagnostics.Outcomes.RateLimited; throw; }
        catch (ProviderUnavailableException) { outcomeTag = BhenguPaymentDiagnostics.Outcomes.Unavailable; throw; }
        finally
        {
            activity.SetOutcome(outcomeTag);
            BhenguPaymentDiagnostics.RefundsTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", outcomeTag));
        }
    }

    /// <inheritdoc/>
    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = BhenguPaymentDiagnostics.StartPayoutActivity(ProviderName, request.Currency);
        var outcomeTag = BhenguPaymentDiagnostics.Outcomes.Error;
        try
        {
            var cached = await TryGetCachedAsync<PayoutResponse>(request.IdempotencyKey, "payout", ct).ConfigureAwait(false);
            if (cached is not null)
            {
                outcomeTag = BhenguPaymentDiagnostics.Outcomes.Success;
                return cached;
            }

            var transactionId = request.IdempotencyKey ?? GenerateTransactionId();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var requestBody = new
            {
                merchant_id = _options.MerchantId,
                transaction_id = transactionId,
                destination_token = request.DestinationToken,
                amount = request.Amount,
                currency = request.Currency,
                description = request.Description,
                timestamp
            };

            var response = await SendSignedRequestAsync($"{_baseUrl}/payouts", requestBody, timestamp, request.IdempotencyKey, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                outcomeTag = BhenguPaymentDiagnostics.Outcomes.RateLimited;
                var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
                throw new ProviderRateLimitException(ProviderName, retryAfter, body);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("BRICS Pay payout failed: {StatusCode} {Body}", response.StatusCode, body);
                if ((int)response.StatusCode is >= 400 and < 500)
                {
                    outcomeTag = BhenguPaymentDiagnostics.Outcomes.Declined;
                    throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), body);
                }
                outcomeTag = BhenguPaymentDiagnostics.Outcomes.Unavailable;
                throw new BhenguPaymentException(
                    ProviderName,
                    $"BRICS Pay payout failed: HTTP {(int)response.StatusCode}",
                    providerErrorCode: ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                    providerErrorMessage: body);
            }

            var result = JsonSerializer.Deserialize<BricsPayApiResponse>(body);
            var status = MapStatus(result?.Status ?? "pending");
            var pr = new PayoutResponse
            {
                GatewayReference = result?.PaymentId ?? transactionId,
                Status = status,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow
            };

            outcomeTag = status switch
            {
                PaymentStatus.Completed => BhenguPaymentDiagnostics.Outcomes.Success,
                PaymentStatus.Pending => BhenguPaymentDiagnostics.Outcomes.Pending,
                PaymentStatus.Failed => BhenguPaymentDiagnostics.Outcomes.Declined,
                _ => BhenguPaymentDiagnostics.Outcomes.Pending
            };
            await TrySetCachedAsync(request.IdempotencyKey, "payout", pr, ct).ConfigureAwait(false);
            return pr;
        }
        catch (PaymentDeclinedException) { outcomeTag = BhenguPaymentDiagnostics.Outcomes.Declined; throw; }
        catch (ProviderRateLimitException) { outcomeTag = BhenguPaymentDiagnostics.Outcomes.RateLimited; throw; }
        catch (ProviderUnavailableException) { outcomeTag = BhenguPaymentDiagnostics.Outcomes.Unavailable; throw; }
        finally
        {
            activity.SetOutcome(outcomeTag);
            BhenguPaymentDiagnostics.PayoutsTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", outcomeTag));
        }
    }

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            _logger.LogWarning("BRICS Pay WebhookSecret not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var computed = Convert.ToBase64String(computedHash);

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(computed));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BRICS Pay webhook signature verification raised");
            return false;
        }
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        using var activity = BhenguPaymentDiagnostics.StartWebhookActivity(ProviderName);
        try
        {
            var webhookEvent = JsonSerializer.Deserialize<BricsPayWebhookPayload>(payload);
            if (webhookEvent is null)
            {
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
                return Task.FromResult<WebhookEvent?>(null);
            }

            var typed = MapWebhookEvent(webhookEvent);
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return Task.FromResult(typed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse BRICS Pay webhook payload");
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private static WebhookEvent? MapWebhookEvent(BricsPayWebhookPayload evt)
    {
        if (string.IsNullOrEmpty(evt.PaymentId)) return null;

        var amount = evt.Amount;
        var currency = string.IsNullOrEmpty(evt.Currency) ? "ZAR" : evt.Currency;
        var category = evt.EventType?.ToLowerInvariant();

        switch (category)
        {
            case "payment.completed":
            case "payment.success":
                return new ChargeSucceededEvent
                {
                    GatewayReference = evt.PaymentId,
                    Status = PaymentStatus.Completed,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency
                };

            case "payment.failed":
                return new ChargeFailedEvent
                {
                    GatewayReference = evt.PaymentId,
                    Status = PaymentStatus.Failed,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = evt.Status,
                    FailureMessage = evt.Status
                };

            case "refund.completed":
            case "refund.success":
                return new RefundSucceededEvent
                {
                    GatewayReference = evt.PaymentId,
                    Status = PaymentStatus.Refunded,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = evt.PaymentId,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false
                };

            case "payout.completed":
            case "payout.success":
                return new PayoutCompletedEvent
                {
                    GatewayReference = evt.PaymentId,
                    Status = PaymentStatus.Completed,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.PayoutCompleted,
                    PayoutReference = evt.PaymentId,
                    Amount = amount,
                    Currency = currency
                };

            case "payout.failed":
                return new PayoutFailedEvent
                {
                    GatewayReference = evt.PaymentId,
                    Status = PaymentStatus.Failed,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.PayoutFailed,
                    PayoutReference = evt.PaymentId,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = evt.Status,
                    FailureMessage = evt.Status
                };

            case "settlement.completed":
                return new SettlementCompletedEvent
                {
                    GatewayReference = evt.PaymentId,
                    Status = PaymentStatus.Completed,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.SettlementCompleted,
                    SettlementReference = evt.PaymentId,
                    NetAmount = amount,
                    Currency = currency
                };

            default:
                var status = MapStatus(evt.Status ?? string.Empty);
                return new WebhookEvent
                {
                    GatewayReference = evt.PaymentId,
                    Status = status,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.Unknown
                };
        }
    }

    private async Task<HttpResponseMessage> SendSignedRequestAsync(string url, object body, long timestamp, string? idempotencyKey, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body);
        var signature = GenerateSignature(json, timestamp);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Merchant-Id", _options.MerchantId);
        req.Headers.Add("X-Signature", signature);
        req.Headers.Add("X-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        try
        {
            return await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to BRICS Pay failed", ex);
        }
    }

    private string GenerateSignature(string serializedBody, long timestamp)
    {
        var payload = serializedBody + timestamp + _options.SecretKey;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }

    private async Task<T?> TryGetCachedAsync<T>(string? idempotencyKey, string operation, CancellationToken ct) where T : class
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return null;
        return await _cache.GetAsync<T>(BuildCacheKey(idempotencyKey, operation), ct).ConfigureAwait(false);
    }

    private async Task TrySetCachedAsync<T>(string? idempotencyKey, string operation, T value, CancellationToken ct) where T : class
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return;
        await _cache.SetAsync(BuildCacheKey(idempotencyKey, operation), value, s_idempotencyTtl, ct).ConfigureAwait(false);
    }

    private static string BuildCacheKey(string idempotencyKey, string operation)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey))).ToLowerInvariant();
        return $"bricspay:idem:{operation}:{hash}";
    }

    private static string GenerateTransactionId() =>
        $"BRICS_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..32];

    private static PaymentStatus MapStatus(string raw) => raw.ToLowerInvariant() switch
    {
        "success" or "completed" or "settled" => PaymentStatus.Completed,
        "pending" or "processing" => PaymentStatus.Pending,
        "failed" or "declined" or "error" => PaymentStatus.Failed,
        "cancelled" or "voided" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    private static BricsCurrency ParseCurrency(string currency) =>
        Enum.TryParse<BricsCurrency>(currency, ignoreCase: true, out var result) ? result : BricsCurrency.ZAR;

    private sealed record BricsPayApiResponse
    {
        public string? PaymentId { get; init; }
        public string? Status { get; init; }
        public string? Message { get; init; }
    }

    private sealed record BricsPayWebhookPayload
    {
        public string? EventType { get; init; }
        public string? PaymentId { get; init; }
        public string? Status { get; init; }
        public decimal Amount { get; init; }
        public string? Currency { get; init; }
    }
}
