// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.ChipperCash.Configuration;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.ChipperCash.Providers;

/// <summary>
/// Chipper Cash (pan-African) payment gateway provider. Wraps the Chipper Cash REST API
/// for mobile-money collections, disbursements and refunds, with HMAC-SHA256 request signing
/// and webhook verification. Honours per-call <c>IdempotencyKey</c> on
/// <see cref="PaymentRequest"/> / <see cref="RefundRequest"/> / <see cref="PayoutRequest"/> by
/// dedup'ing via the shared <see cref="IBhenguDistributedCache"/> for 24 hours.
/// </summary>
public sealed class ChipperCashPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly ChipperCashOptions _options;
    private readonly IBhenguDistributedCache _cache;
    private static readonly TimeSpan s_idempotencyTtl = TimeSpan.FromHours(24);

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.ChipperCash;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.CrossBorder |
        ProviderCapabilities.Tokenisation |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public ChipperCashPaymentProvider(
        HttpClient httpClient,
        IOptions<ChipperCashOptions> options,
        ILogger<ChipperCashPaymentProvider> logger,
        IBhenguDistributedCache? cache = null)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _cache = cache ?? new InMemoryBhenguDistributedCache();

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(ChipperCashOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.ApiSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(ChipperCashOptions.ApiSecret)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.chippercash.com/");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(_options.ApiKey);
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, async () =>
        {
            var cachedResponse = await TryGetCachedAsync<PaymentResponse>(request.IdempotencyKey, "charge", ct).ConfigureAwait(false);
            if (cachedResponse is not null) return cachedResponse;

            var reference = request.Metadata?.GetValueOrDefault("reference") ?? $"chp-{Guid.NewGuid():N}";
            var msisdn = request.Metadata?.GetValueOrDefault("msisdn") ?? request.PaymentMethodToken;
            var network = request.Metadata?.GetValueOrDefault("network") ?? "MTN";
            var country = request.Metadata?.GetValueOrDefault("country") ?? _options.Country;

            var requestBody = new
            {
                amount = request.Amount,
                currency = request.Currency.ToUpperInvariant(),
                country,
                mobile = new { msisdn, network },
                reference,
                callbackUrl = _options.CallbackUrl
            };

            var body = await SendAsync(HttpMethod.Post, "v1/collections", requestBody, ct, "ProcessPayment").ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<ChipperCashCollectionResponse>(body);

            Logger.LogInformation("Chipper collection created: {Id} status={Status}", resp?.Id, resp?.Status);

            var status = MapStatus(resp?.Status);
            var paymentResponse = new PaymentResponse
            {
                GatewayReference = resp?.Id ?? reference,
                Status = status,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                Message = resp?.Message
            };

            await TrySetCachedAsync(request.IdempotencyKey, "charge", paymentResponse, ct).ConfigureAwait(false);
            return paymentResponse;
        }, ct);
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunRefundAsync(request.GatewayReference, async () =>
        {
            var cachedResponse = await TryGetCachedAsync<RefundResponse>(request.IdempotencyKey, "refund", ct).ConfigureAwait(false);
            if (cachedResponse is not null) return cachedResponse;

            var requestBody = new { amount = request.Amount, reason = request.Reason };
            var path = $"v1/collections/{Uri.EscapeDataString(request.GatewayReference)}/refund";
            var body = await SendAsync(HttpMethod.Post, path, requestBody, ct, "ProcessRefund").ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<ChipperCashCollectionResponse>(body);

            Logger.LogInformation("Chipper refund created: {Id} for {OriginalRef}", resp?.Id, request.GatewayReference);

            var refundResponse = new RefundResponse
            {
                GatewayReference = resp?.Id ?? string.Empty,
                Amount = request.Amount,
                Status = MapStatus(resp?.Status),
                ProcessedAt = DateTime.UtcNow,
                Message = resp?.Message
            };

            await TrySetCachedAsync(request.IdempotencyKey, "refund", refundResponse, ct).ConfigureAwait(false);
            return refundResponse;
        }, ct);
    }

    /// <inheritdoc/>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunPayoutAsync(request.Currency, async () =>
        {
            var cachedResponse = await TryGetCachedAsync<PayoutResponse>(request.IdempotencyKey, "payout", ct).ConfigureAwait(false);
            if (cachedResponse is not null) return cachedResponse;

            var msisdn = request.DestinationToken;
            var requestBody = new
            {
                amount = request.Amount,
                currency = request.Currency.ToUpperInvariant(),
                destination = new
                {
                    country = _options.Country,
                    mobile = new { msisdn },
                    name = "Bhengu Beneficiary",
                    email = "noreply@bhengu.example"
                },
                reference = request.IdempotencyKey ?? $"chp-payout-{Guid.NewGuid():N}",
                callbackUrl = _options.CallbackUrl
            };

            var body = await SendAsync(HttpMethod.Post, "v1/disbursements", requestBody, ct, "ProcessPayout").ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<ChipperCashCollectionResponse>(body);

            Logger.LogInformation("Chipper disbursement created: {Id} status={Status}", resp?.Id, resp?.Status);

            var payoutResponse = new PayoutResponse
            {
                GatewayReference = resp?.Id ?? string.Empty,
                Status = MapStatus(resp?.Status),
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow
            };

            await TrySetCachedAsync(request.IdempotencyKey, "payout", payoutResponse, ct).ConfigureAwait(false);
            return payoutResponse;
        }, ct);
    }

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        return RunWebhookVerify(() =>
        {
            if (string.IsNullOrWhiteSpace(_options.ApiSecret))
            {
                Logger.LogWarning("Chipper ApiSecret not configured — signature verification cannot succeed.");
                return false;
            }
            return SignatureHelpers.VerifyHmacSha256(payload, signature, _options.ApiSecret);
        });
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return RunOperationAsync("parse_webhook", () =>
        {
            try
            {
                var evt = JsonSerializer.Deserialize<ChipperCashWebhookEvent>(payload);
                if (evt is null) return Task.FromResult<WebhookEvent?>(null);

                Logger.LogInformation("Parsed Chipper webhook event: {EventType}", evt.Event);
                var typed = MapWebhookEvent(evt);
                return Task.FromResult(typed);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to parse Chipper webhook event");
                return Task.FromResult<WebhookEvent?>(null);
            }
        }, ct);
    }

    private static WebhookEvent? MapWebhookEvent(ChipperCashWebhookEvent evt)
    {
        var eventName = evt.Event?.ToLowerInvariant();
        var reference = evt.Data?.Id ?? string.Empty;
        if (string.IsNullOrEmpty(reference)) return null;

        var amount = evt.Data?.Amount ?? 0m;
        var currency = evt.Data?.Currency ?? "NGN";

        switch (eventName)
        {
            case "collection.successful":
            case "payment.successful":
                return new ChargeSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency,
                    CustomerId = evt.Data?.Mobile?.Msisdn,
                    PaymentMethodToken = evt.Data?.Mobile?.Msisdn
                };

            case "collection.failed":
            case "payment.failed":
                return new ChargeFailedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Failed,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = evt.Data?.Status,
                    FailureMessage = evt.Data?.Message
                };

            case "refund.successful":
            case "refund.processed":
                return new RefundSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Refunded,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = reference,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false
                };

            case "disbursement.successful":
                return new PayoutCompletedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.PayoutCompleted,
                    PayoutReference = reference,
                    Amount = amount,
                    Currency = currency,
                    DestinationToken = evt.Data?.Mobile?.Msisdn
                };

            case "disbursement.failed":
                return new PayoutFailedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Failed,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.PayoutFailed,
                    PayoutReference = reference,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = evt.Data?.Status,
                    FailureMessage = evt.Data?.Message
                };

            default:
                var status = eventName switch
                {
                    "collection.successful" or "disbursement.successful" or "payment.successful" => PaymentStatus.Completed,
                    "collection.failed" or "disbursement.failed" or "payment.failed" => PaymentStatus.Failed,
                    "refund.successful" or "refund.processed" => PaymentStatus.Refunded,
                    _ => (PaymentStatus?)null
                };
                if (status is null) return null;
                return new WebhookEvent
                {
                    GatewayReference = reference,
                    Status = status.Value,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.Unknown
                };
        }
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        // HMAC-SHA256 of "<body>.<timestamp>" using ApiSecret, lowercase hex, in X-Chipper-Signature.
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.ApiSecret));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(json + "." + timestamp))).ToLowerInvariant();
        req.Headers.TryAddWithoutValidation("X-Chipper-Signature", signature);
        req.Headers.TryAddWithoutValidation("X-Chipper-Timestamp", timestamp);
        if (!string.IsNullOrWhiteSpace(_options.MerchantId))
            req.Headers.TryAddWithoutValidation("X-Chipper-Merchant-Id", _options.MerchantId);

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Chipper {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private async Task<T?> TryGetCachedAsync<T>(string? idempotencyKey, string operation, CancellationToken ct) where T : class
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return null;
        var cacheKey = BuildCacheKey(idempotencyKey, operation);
        return await _cache.GetAsync<T>(cacheKey, ct).ConfigureAwait(false);
    }

    private async Task TrySetCachedAsync<T>(string? idempotencyKey, string operation, T value, CancellationToken ct) where T : class
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return;
        var cacheKey = BuildCacheKey(idempotencyKey, operation);
        await _cache.SetAsync(cacheKey, value, s_idempotencyTtl, ct).ConfigureAwait(false);
    }

    private static string BuildCacheKey(string idempotencyKey, string operation)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return $"chippercash:idem:{operation}:{hash}";
    }

    private static PaymentStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "successful" or "success" or "completed" => PaymentStatus.Completed,
        "pending" or "processing" or "initiated" => PaymentStatus.Pending,
        "failed" or "declined" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" or "refund_processed" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Chipper Cash API response shapes (internal) ===

    private sealed class ChipperCashCollectionResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
    }

    private sealed class ChipperCashWebhookEvent
    {
        [JsonPropertyName("event")] public string? Event { get; set; }
        [JsonPropertyName("data")] public ChipperCashWebhookData? Data { get; set; }
    }

    private sealed class ChipperCashWebhookData
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("mobile")] public ChipperCashWebhookMobile? Mobile { get; set; }
    }

    private sealed class ChipperCashWebhookMobile
    {
        [JsonPropertyName("msisdn")] public string? Msisdn { get; set; }
        [JsonPropertyName("network")] public string? Network { get; set; }
    }
}
