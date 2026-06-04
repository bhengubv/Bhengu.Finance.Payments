// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Slydepay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Slydepay.Providers;

/// <summary>
/// Slydepay (Ghana) mobile-first wallet provider. Wraps the legacy paymentservice.asmx
/// JSON API: ProcessPaymentOrder, VerifyTransactionStatus, CancelTransactionStatus.
/// Slydepay has no native refund or payout API; both throw.
/// Webhook authenticity is verified by re-calling VerifyTransactionStatus (no HMAC is issued).
/// </summary>
public sealed class SlydepayPaymentProvider : IPaymentGatewayProvider
{
    private readonly HttpClient _httpClient;
    private readonly SlydepayOptions _options;
    private readonly ILogger<SlydepayPaymentProvider> _logger;
    private readonly IBhenguDistributedCache? _idempotencyCache;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Slydepay;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the Slydepay payment provider. <paramref name="idempotencyCache"/> is optional — when omitted, idempotency replay is a no-op.</summary>
    public SlydepayPaymentProvider(
        HttpClient httpClient,
        IOptions<SlydepayOptions> options,
        ILogger<SlydepayPaymentProvider> logger,
        IBhenguDistributedCache? idempotencyCache = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _idempotencyCache = idempotencyCache;

        if (string.IsNullOrWhiteSpace(_options.EmailOrMobile))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(SlydepayOptions.EmailOrMobile)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(SlydepayOptions.MerchantKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var raw = _options.UseSandbox
                ? _options.SandboxUrl ?? "https://uat.slydepay.com.gh/"
                : _options.BaseUrl ?? "https://app.slydepay.com.gh/";
            if (!raw.EndsWith('/')) raw += "/";
            _httpClient.BaseAddress = new Uri(raw);
        }
    }

    /// <inheritdoc/>
    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = BhenguPaymentDiagnostics.StartChargeActivity(ProviderName, request.Currency);
        var started = DateTime.UtcNow;
        var cached = await TryGetCachedAsync<PaymentResponse>(request.IdempotencyKey, "charge", ct).ConfigureAwait(false);
        if (cached is not null)
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return cached;
        }

        try
        {
            var body = new
            {
                emailOrMobileNumber = _options.EmailOrMobile,
                merchantKey = _options.MerchantKey,
                orderCode = request.PaymentMethodToken,
                description = request.Description,
                amount = request.Amount,
                comment1 = request.Metadata?.GetValueOrDefault("comment1") ?? "",
                comment2 = request.Metadata?.GetValueOrDefault("comment2") ?? "",
                surcharge = request.Metadata?.GetValueOrDefault("surcharge") ?? "0",
                currency = request.Currency.ToUpperInvariant(),
                paymentChannels = _options.PaymentChannels
            };

            var responseBody = await SendAsync(HttpMethod.Post, "webservices/paymentservice.asmx/ProcessPaymentOrder",
                body, ct, "ProcessPayment").ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<SlydepayEnvelope<SlydepayProcessResult>>(responseBody);
            var result = envelope?.Result;

            _logger.LogInformation("Slydepay ProcessPaymentOrder: success={Success} payToken={PayToken}",
                result?.Success, result?.PayToken);

            var response = new PaymentResponse
            {
                GatewayReference = result?.PayToken ?? request.PaymentMethodToken,
                Status = result?.Success == true ? PaymentStatus.Pending : PaymentStatus.Failed,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                RedirectUrl = result?.CheckOutUrl,
                Message = envelope?.ErrorMessage
            };

            await TrySetCachedAsync(request.IdempotencyKey, "charge", response, ct).ConfigureAwait(false);
            var outcome = response.Status == PaymentStatus.Pending
                ? BhenguPaymentDiagnostics.Outcomes.Pending
                : BhenguPaymentDiagnostics.Outcomes.Declined;
            BhenguPaymentDiagnostics.ChargesTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", outcome));
            activity.SetOutcome(outcome);
            return response;
        }
        catch (Exception ex)
        {
            var outcome = ClassifyOutcome(ex);
            BhenguPaymentDiagnostics.ChargesTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", outcome));
            activity.SetOutcome(outcome);
            throw;
        }
        finally
        {
            BhenguPaymentDiagnostics.ChargeDurationMs.Record(
                (DateTime.UtcNow - started).TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", ProviderName));
        }
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        throw new BhenguPaymentException(
            ProviderName,
            "Slydepay does not natively support refunds via the public API — issue refunds via the Slydepay merchant portal.",
            providerErrorCode: "not_supported");
    }

    /// <summary>
    /// Verify a Slydepay transaction's status via VerifyTransactionStatus. Returns the raw
    /// envelope body — callers can deserialise/inspect for fine-grained status fields.
    /// </summary>
    public async Task<string> VerifyTransactionAsync(string payToken, string orderCode, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payToken);
        ArgumentException.ThrowIfNullOrEmpty(orderCode);

        var body = new
        {
            emailOrMobileNumber = _options.EmailOrMobile,
            merchantKey = _options.MerchantKey,
            payToken,
            orderCode
        };
        return await SendAsync(HttpMethod.Post, "webservices/paymentservice.asmx/VerifyTransactionStatus",
            body, ct, "VerifyTransaction").ConfigureAwait(false);
    }

    /// <summary>
    /// Cancel a pending Slydepay transaction via CancelTransactionStatus.
    /// </summary>
    public async Task<string> CancelTransactionAsync(string payToken, string orderCode, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payToken);
        ArgumentException.ThrowIfNullOrEmpty(orderCode);

        var body = new
        {
            emailOrMobileNumber = _options.EmailOrMobile,
            merchantKey = _options.MerchantKey,
            payToken,
            orderCode
        };
        return await SendAsync(HttpMethod.Post, "webservices/paymentservice.asmx/CancelTransactionStatus",
            body, ct, "CancelTransaction").ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.MerchantKey))
        {
            _logger.LogWarning("Slydepay MerchantKey not configured — webhook verification cannot succeed.");
            BhenguPaymentDiagnostics.WebhookVerificationsTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("valid", false));
            return false;
        }

        // Slydepay does NOT HMAC its PaymentNotificationUrl callbacks. Constant-time compare the
        // supplied signature with the configured MerchantKey, and additionally require callers to
        // re-confirm via VerifyTransactionAsync(payToken, orderCode) in production.
        var a = Encoding.UTF8.GetBytes(signature);
        var b = Encoding.UTF8.GetBytes(_options.MerchantKey);
        var valid = a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        BhenguPaymentDiagnostics.WebhookVerificationsTotal.Add(1,
            new KeyValuePair<string, object?>("provider", ProviderName),
            new KeyValuePair<string, object?>("valid", valid));
        return valid;
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        using var activity = BhenguPaymentDiagnostics.StartWebhookActivity(ProviderName);

        try
        {
            var notif = JsonSerializer.Deserialize<SlydepayNotification>(payload);
            if (notif is null || string.IsNullOrEmpty(notif.PayToken))
                return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Slydepay notification: payToken={PayToken} status={Status}",
                notif.PayToken, notif.TransactionStatus);

            var statusUpper = notif.TransactionStatus?.ToUpperInvariant();
            var currency = notif.Currency ?? _options.Currency;
            WebhookEvent? typed = statusUpper switch
            {
                "CONFIRMED" or "PAID" or "COMPLETED" or "SUCCESS" => new ChargeSucceededEvent
                {
                    GatewayReference = notif.PayToken,
                    Status = PaymentStatus.Completed,
                    EventType = notif.TransactionStatus,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = notif.Amount,
                    Currency = currency
                },
                "PENDING" or "PROCESSING" or "ACCEPTED" => new ChargePendingEvent
                {
                    GatewayReference = notif.PayToken,
                    Status = PaymentStatus.Pending,
                    EventType = notif.TransactionStatus,
                    Category = WebhookEventCategory.ChargePending,
                    Amount = notif.Amount,
                    Currency = currency
                },
                "FAILED" or "DECLINED" => new ChargeFailedEvent
                {
                    GatewayReference = notif.PayToken,
                    Status = PaymentStatus.Failed,
                    EventType = notif.TransactionStatus,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = notif.Amount,
                    Currency = currency,
                    FailureMessage = notif.TransactionStatus
                },
                "CANCELED" or "CANCELLED" => new WebhookEvent
                {
                    GatewayReference = notif.PayToken,
                    Status = PaymentStatus.Cancelled,
                    EventType = notif.TransactionStatus,
                    Category = WebhookEventCategory.Unknown
                },
                "REFUNDED" => new RefundSucceededEvent
                {
                    GatewayReference = notif.PayToken,
                    Status = PaymentStatus.Refunded,
                    EventType = notif.TransactionStatus,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = notif.PayToken,
                    Amount = notif.Amount,
                    Currency = currency,
                    IsPartial = false
                },
                _ => null
            };

            return Task.FromResult(typed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Slydepay notification");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Slydepay failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Slydepay {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private async Task<T?> TryGetCachedAsync<T>(string? idempotencyKey, string operation, CancellationToken ct) where T : class
    {
        if (_idempotencyCache is null || string.IsNullOrWhiteSpace(idempotencyKey))
            return null;
        var key = $"slydepay:{operation}:{idempotencyKey}";
        return await _idempotencyCache.GetAsync<T>(key, ct).ConfigureAwait(false);
    }

    private async Task TrySetCachedAsync<T>(string? idempotencyKey, string operation, T value, CancellationToken ct) where T : class
    {
        if (_idempotencyCache is null || string.IsNullOrWhiteSpace(idempotencyKey))
            return;
        var key = $"slydepay:{operation}:{idempotencyKey}";
        await _idempotencyCache.SetAsync(key, value, TimeSpan.FromHours(24), ct).ConfigureAwait(false);
    }

    private static string ClassifyOutcome(Exception ex) => ex switch
    {
        PaymentDeclinedException => BhenguPaymentDiagnostics.Outcomes.Declined,
        ProviderRateLimitException => BhenguPaymentDiagnostics.Outcomes.RateLimited,
        ProviderUnavailableException => BhenguPaymentDiagnostics.Outcomes.Unavailable,
        _ => BhenguPaymentDiagnostics.Outcomes.Error
    };

    private sealed class SlydepayEnvelope<T>
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; set; }
        [JsonPropertyName("result")] public T? Result { get; set; }
    }

    private sealed class SlydepayProcessResult
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("payToken")] public string? PayToken { get; set; }
        [JsonPropertyName("qrCode")] public string? QrCode { get; set; }
        [JsonPropertyName("checkOutUrl")] public string? CheckOutUrl { get; set; }
    }

    private sealed class SlydepayNotification
    {
        [JsonPropertyName("payToken")] public string? PayToken { get; set; }
        [JsonPropertyName("orderCode")] public string? OrderCode { get; set; }
        [JsonPropertyName("transactionStatus")] public string? TransactionStatus { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }
}
