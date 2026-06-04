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
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.BricsPay.Providers;

/// <summary>
/// BRICS Pay cross-border payments. Supports payments and payouts within BRICS nations
/// (South Africa, Brazil, Russia, India, China) with automatic currency conversion. Honours
/// per-call <c>IdempotencyKey</c> via the shared <see cref="IBhenguDistributedCache"/> for 24
/// hours.
/// </summary>
public sealed class BricsPayPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly BricsPayOptions _options;
    private readonly ICurrencyExchangeService _exchangeService;
    private readonly IBhenguDistributedCache _cache;
    private readonly string _baseUrl;
    private static readonly TimeSpan s_idempotencyTtl = TimeSpan.FromHours(24);

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.BricsPay;

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
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _exchangeService = exchangeService ?? throw new ArgumentNullException(nameof(exchangeService));
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
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, async () =>
        {
            var cached = await TryGetCachedAsync<PaymentResponse>(request.IdempotencyKey, "charge", ct).ConfigureAwait(false);
            if (cached is not null) return cached;

            var sourceCurrency = ParseCurrency(request.Currency);
            var targetCurrency = request.Metadata?.TryGetValue("target_currency", out var tc) == true
                ? ParseCurrency(tc)
                : sourceCurrency;

            ConversionResult? conversion = null;
            if (sourceCurrency != targetCurrency)
            {
                conversion = await _exchangeService.LockRateAsync(request.Amount, sourceCurrency, targetCurrency, ct: ct).ConfigureAwait(false);
                Logger.LogInformation("Currency conversion {Amount} {From} -> {Final} {To} @ {Rate}",
                    request.Amount, sourceCurrency, conversion.FinalAmount, targetCurrency, conversion.ExchangeRate);
            }

            var transactionId = request.IdempotencyKey ?? GenerateTransactionId();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var requestBody = new BricsPayChargeRequest
            {
                MerchantId = _options.MerchantId,
                TransactionId = transactionId,
                PaymentMethodToken = request.PaymentMethodToken,
                Amount = conversion?.FinalAmount ?? request.Amount,
                Currency = targetCurrency.ToString(),
                SourceCurrency = sourceCurrency.ToString(),
                SourceAmount = request.Amount,
                ExchangeRate = conversion?.ExchangeRate ?? 1m,
                QuoteId = conversion?.QuoteId,
                Description = request.Description,
                Metadata = request.Metadata,
                Timestamp = timestamp
            };

            var body = await SendSignedRequestAsync($"{_baseUrl}/payments", requestBody, timestamp, request.IdempotencyKey, ct).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<BricsPayApiResponse>(body, s_jsonOptions);

            var pr = new PaymentResponse
            {
                GatewayReference = result?.PaymentId ?? transactionId,
                Status = MapStatus(result?.Status ?? "pending"),
                Amount = conversion?.FinalAmount ?? request.Amount,
                Currency = targetCurrency.ToString(),
                ProcessedAt = DateTime.UtcNow,
                Message = result?.Message
            };

            await TrySetCachedAsync(request.IdempotencyKey, "charge", pr, ct).ConfigureAwait(false);
            return pr;
        }, ct);
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunRefundAsync(request.GatewayReference, async () =>
        {
            var cached = await TryGetCachedAsync<RefundResponse>(request.IdempotencyKey, "refund", ct).ConfigureAwait(false);
            if (cached is not null) return cached;

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var requestBody = new BricsPayRefundRequest
            {
                MerchantId = _options.MerchantId,
                OriginalPaymentId = request.GatewayReference,
                RefundAmount = request.Amount,
                Reason = request.Reason,
                Timestamp = timestamp
            };

            var body = await SendSignedRequestAsync($"{_baseUrl}/refunds", requestBody, timestamp, request.IdempotencyKey, ct).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<BricsPayApiResponse>(body, s_jsonOptions);

            var rr = new RefundResponse
            {
                GatewayReference = result?.PaymentId ?? $"REFUND_{Guid.NewGuid():N}",
                Amount = request.Amount,
                Status = MapStatus(result?.Status ?? "pending"),
                ProcessedAt = DateTime.UtcNow,
                Message = result?.Message
            };

            await TrySetCachedAsync(request.IdempotencyKey, "refund", rr, ct).ConfigureAwait(false);
            return rr;
        }, ct);
    }

    /// <inheritdoc/>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunPayoutAsync(request.Currency, async () =>
        {
            var cached = await TryGetCachedAsync<PayoutResponse>(request.IdempotencyKey, "payout", ct).ConfigureAwait(false);
            if (cached is not null) return cached;

            var transactionId = request.IdempotencyKey ?? GenerateTransactionId();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var requestBody = new BricsPayPayoutRequest
            {
                MerchantId = _options.MerchantId,
                TransactionId = transactionId,
                DestinationToken = request.DestinationToken,
                Amount = request.Amount,
                Currency = request.Currency,
                Description = request.Description,
                Timestamp = timestamp
            };

            var body = await SendSignedRequestAsync($"{_baseUrl}/payouts", requestBody, timestamp, request.IdempotencyKey, ct).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<BricsPayApiResponse>(body, s_jsonOptions);

            var pr = new PayoutResponse
            {
                GatewayReference = result?.PaymentId ?? transactionId,
                Status = MapStatus(result?.Status ?? "pending"),
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow
            };

            await TrySetCachedAsync(request.IdempotencyKey, "payout", pr, ct).ConfigureAwait(false);
            return pr;
        }, ct);
    }

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        return RunWebhookVerify(() =>
        {
            if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
            {
                Logger.LogWarning("BRICS Pay WebhookSecret not configured — signature verification cannot succeed.");
                return false;
            }
            // BRICS Pay signs the raw payload with HMAC-SHA256 and Base64-encodes the digest.
            return SignatureHelpers.VerifyHmacSha256(payload, signature, _options.WebhookSecret, SignatureHelpers.Encoding.Base64);
        });
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        using var activity = BhenguPaymentDiagnostics.StartWebhookActivity(ProviderName);
        try
        {
            var webhookEvent = JsonSerializer.Deserialize<BricsPayWebhookPayload>(payload, s_jsonOptions);
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
            Logger.LogError(ex, "Failed to parse BRICS Pay webhook payload");
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

    private async Task<string> SendSignedRequestAsync(string url, object body, long timestamp, string? idempotencyKey, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, s_jsonOptions);
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

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("BRICS Pay request failed: {StatusCode} {Body}", response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
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

    private sealed record BricsPayChargeRequest
    {
        public string? MerchantId { get; init; }
        public string? TransactionId { get; init; }
        public string? PaymentMethodToken { get; init; }
        public decimal Amount { get; init; }
        public string? Currency { get; init; }
        public string? SourceCurrency { get; init; }
        public decimal SourceAmount { get; init; }
        public decimal ExchangeRate { get; init; }
        public string? QuoteId { get; init; }
        public string? Description { get; init; }
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
        public long Timestamp { get; init; }
    }

    private sealed record BricsPayRefundRequest
    {
        public string? MerchantId { get; init; }
        public string? OriginalPaymentId { get; init; }
        public decimal RefundAmount { get; init; }
        public string? Reason { get; init; }
        public long Timestamp { get; init; }
    }

    private sealed record BricsPayPayoutRequest
    {
        public string? MerchantId { get; init; }
        public string? TransactionId { get; init; }
        public string? DestinationToken { get; init; }
        public decimal Amount { get; init; }
        public string? Currency { get; init; }
        public string? Description { get; init; }
        public long Timestamp { get; init; }
    }

    // Responses come back from BRICS Pay in PascalCase, so override the global snake_case policy
    // explicitly on these read-only shapes.
    private sealed record BricsPayApiResponse
    {
        [JsonPropertyName("PaymentId")] public string? PaymentId { get; init; }
        [JsonPropertyName("Status")] public string? Status { get; init; }
        [JsonPropertyName("Message")] public string? Message { get; init; }
    }

    private sealed record BricsPayWebhookPayload
    {
        [JsonPropertyName("EventType")] public string? EventType { get; init; }
        [JsonPropertyName("PaymentId")] public string? PaymentId { get; init; }
        [JsonPropertyName("Status")] public string? Status { get; init; }
        [JsonPropertyName("Amount")] public decimal Amount { get; init; }
        [JsonPropertyName("Currency")] public string? Currency { get; init; }
    }
}
