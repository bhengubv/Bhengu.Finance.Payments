// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Security;
using Bhengu.Finance.Payments.Onafriq.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Onafriq.Providers;

/// <summary>
/// Onafriq (formerly MFS Africa) cross-border mobile-money provider. Onafriq is primarily a
/// transfer / disbursement rail (wallet-to-wallet across 35+ African countries) — the payout path
/// is the canonical use. ProcessPaymentAsync maps to the <c>/v1/collections</c> endpoint.
/// Refunds are not supported by Onafriq: money movement is one-directional and reversals require
/// a new opposite transaction.
/// </summary>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "Wire format built from public documentation; never sandbox-verified.")]
public sealed class OnafriqPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly OnafriqOptions _options;
    private readonly IBhenguDistributedCache _cache;
    private static readonly TimeSpan s_idempotencyTtl = TimeSpan.FromHours(24);

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Onafriq;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.CrossBorder |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public OnafriqPaymentProvider(
        HttpClient httpClient,
        IOptions<OnafriqOptions> options,
        ILogger<OnafriqPaymentProvider> logger,
        IBhenguDistributedCache? cache = null)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _cache = cache ?? new InMemoryBhenguDistributedCache();

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OnafriqOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OnafriqOptions.MerchantId)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var defaultUrl = _options.UseSandbox
                ? "https://api-sandbox.onafriq.com/"
                : "https://api.onafriq.com/";
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? defaultUrl);
        }

        _httpClient.DefaultRequestHeaders.Remove("X-API-Key");
        _httpClient.DefaultRequestHeaders.Add("X-API-Key", _options.ApiKey);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, async () =>
        {
            var cached = await TryGetCachedAsync<PaymentResponse>(request.IdempotencyKey, "charge", ct).ConfigureAwait(false);
            if (cached is not null) return cached;

            // PaymentMethodToken format: "<country>:<walletNumber>" (e.g. "ZA:27710000000").
            var (countryCode, walletNumber) = SplitDestination(request.PaymentMethodToken, defaultCountry: "ZA");

            var requestBody = new
            {
                merchantId = _options.MerchantId,
                source = new { type = "wallet", country = countryCode, walletNumber },
                amount = request.Amount,
                currency = request.Currency.ToUpperInvariant(),
                reference = request.IdempotencyKey ?? $"col-{Guid.NewGuid():N}",
                description = request.Description,
                callbackUrl = _options.CallbackUrl
            };

            var body = await SendAsync(HttpMethod.Post, "v1/collections", requestBody, ct, "ProcessPayment", request.IdempotencyKey).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<OnafriqTransactionResponse>(body);

            Logger.LogInformation("Onafriq collection initiated: {Id} status={Status}",
                response?.TransactionId, response?.Status);

            var pr = new PaymentResponse
            {
                GatewayReference = response?.TransactionId ?? string.Empty,
                Status = MapStatus(response?.Status ?? "pending"),
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                Message = response?.Status
            };

            await TrySetCachedAsync(request.IdempotencyKey, "charge", pr, ct).ConfigureAwait(false);
            return pr;
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

            // DestinationToken format: "<country>:<walletNumber>" (e.g. "GH:233244000000").
            var (destCountry, destWallet) = SplitDestination(request.DestinationToken, defaultCountry: "GH");

            var requestBody = new
            {
                merchantId = _options.MerchantId,
                source = new
                {
                    type = "wallet",
                    country = "ZA",
                    walletNumber = _options.MerchantId
                },
                destination = new
                {
                    type = "wallet",
                    country = destCountry,
                    walletNumber = destWallet
                },
                amount = request.Amount,
                currency = request.Currency.ToUpperInvariant(),
                reference = request.IdempotencyKey ?? $"pay-{Guid.NewGuid():N}",
                description = request.Description,
                callbackUrl = _options.CallbackUrl
            };

            var body = await SendAsync(HttpMethod.Post, "v1/transactions", requestBody, ct, "ProcessPayout", request.IdempotencyKey).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<OnafriqTransactionResponse>(body);

            Logger.LogInformation("Onafriq transfer initiated: {Id} status={Status}",
                response?.TransactionId, response?.Status);

            var pr = new PayoutResponse
            {
                GatewayReference = response?.TransactionId ?? string.Empty,
                Status = MapStatus(response?.Status ?? "pending"),
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow
            };

            await TrySetCachedAsync(request.IdempotencyKey, "payout", pr, ct).ConfigureAwait(false);
            return pr;
        }, ct);
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        // Onafriq money movement is one-directional. There is no /refund endpoint; reversals must
        // be performed as a new opposite transaction (a payout from your merchant wallet to the
        // original payer's wallet). Surface this explicitly so callers do not silently lose money.
        throw new BhenguPaymentException(
            ProviderName,
            "Onafriq does not support refunds; reversals require a new opposite transaction. " +
            "Issue a payout from your merchant wallet back to the original payer's wallet instead.",
            providerErrorCode: "refund_unsupported");
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
                Logger.LogWarning("Onafriq WebhookSecret not configured — signature verification cannot succeed.");
                return false;
            }
            return SignatureHelpers.VerifyHmacSha256(payload, signature, _options.WebhookSecret);
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
                var webhookEvent = JsonSerializer.Deserialize<OnafriqWebhookEvent>(payload);
                if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

                Logger.LogInformation("Parsed Onafriq webhook event: {EventType}", webhookEvent.EventType);
                var typed = MapWebhookEvent(webhookEvent);
                return Task.FromResult(typed);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to parse Onafriq webhook event");
                return Task.FromResult<WebhookEvent?>(null);
            }
        }, ct);
    }

    private static WebhookEvent? MapWebhookEvent(OnafriqWebhookEvent webhookEvent)
    {
        var reference = webhookEvent.Data?.TransactionId;
        if (string.IsNullOrEmpty(reference)) return null;

        var amount = webhookEvent.Data?.Amount ?? 0m;
        var currency = webhookEvent.Data?.Currency ?? "USD";
        var eventName = webhookEvent.EventType?.ToLowerInvariant();

        switch (eventName)
        {
            case "collection.completed":
            case "collection.successful":
                return new ChargeSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency,
                    CustomerId = webhookEvent.Data?.Source?.WalletNumber,
                    PaymentMethodToken = webhookEvent.Data?.Source?.WalletNumber
                };

            case "collection.failed":
                return new ChargeFailedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = webhookEvent.Data?.Status,
                    FailureMessage = webhookEvent.Data?.Status
                };

            case "transaction.completed":
            case "transaction.successful":
                return new PayoutCompletedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.PayoutCompleted,
                    PayoutReference = reference,
                    Amount = amount,
                    Currency = currency,
                    DestinationToken = webhookEvent.Data?.Destination?.WalletNumber
                };

            case "transaction.failed":
            case "transaction.rejected":
                return new PayoutFailedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.PayoutFailed,
                    PayoutReference = reference,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = webhookEvent.Data?.Status,
                    FailureMessage = webhookEvent.Data?.Status
                };

            default:
                var status = eventName switch
                {
                    "transaction.pending" => PaymentStatus.Pending,
                    _ => (PaymentStatus?)null
                };
                if (status is null) return null;
                return new WebhookEvent
                {
                    GatewayReference = reference,
                    Status = status.Value,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.Unknown
                };
        }
    }

    private static (string Country, string Wallet) SplitDestination(string token, string defaultCountry)
    {
        var colon = token.IndexOf(':');
        if (colon <= 0)
            return (defaultCountry, token);
        return (token[..colon], token[(colon + 1)..]);
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation, string? idempotencyKey)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

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
            Logger.LogError("Onafriq {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
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
        return $"onafriq:idem:{operation}:{hash}";
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "completed" or "successful" or "success" => PaymentStatus.Completed,
        "pending" or "processing" or "initiated" or "submitted" => PaymentStatus.Pending,
        "failed" or "rejected" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        _ => PaymentStatus.Pending
    };

    // === Onafriq API response shapes (internal) ===

    private sealed class OnafriqTransactionResponse
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
    }

    private sealed class OnafriqWebhookEvent
    {
        [JsonPropertyName("eventType")] public string? EventType { get; set; }
        [JsonPropertyName("data")] public OnafriqWebhookData? Data { get; set; }
    }

    private sealed class OnafriqWebhookData
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("amount")] public decimal? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("source")] public OnafriqWebhookEndpoint? Source { get; set; }
        [JsonPropertyName("destination")] public OnafriqWebhookEndpoint? Destination { get; set; }
    }

    private sealed class OnafriqWebhookEndpoint
    {
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("walletNumber")] public string? WalletNumber { get; set; }
    }
}
