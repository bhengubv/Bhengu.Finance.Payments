// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Cellulant.Configuration;
using Bhengu.Finance.Payments.Cellulant.Internals;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Cellulant.Providers;

/// <summary>
/// Cellulant (Tingg / Mula) pan-African aggregator. Wraps the Tingg Express Checkout v3 API for
/// collections and refunds, and the Mula disbursement endpoint for payouts. OAuth2 access tokens
/// are minted on demand using the configured client credentials. Webhooks are HMAC-SHA256 signed
/// via the <c>x-tingg-signature</c> header. Honours per-call <c>IdempotencyKey</c> by dedup'ing
/// via the shared <see cref="IBhenguDistributedCache"/> for 24 hours.
/// </summary>
public sealed class CellulantPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly CellulantOptions _options;
    private readonly ILogger<CellulantPaymentProvider> _logger;
    private readonly IBhenguDistributedCache _cache;
    private readonly CellulantTokenBroker _tokenBroker;
    private static readonly TimeSpan s_idempotencyTtl = TimeSpan.FromHours(24);

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Cellulant;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.CrossBorder |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.Marketplace |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public CellulantPaymentProvider(
        HttpClient httpClient,
        IOptions<CellulantOptions> options,
        ILogger<CellulantPaymentProvider> logger,
        IBhenguDistributedCache? cache = null,
        CellulantTokenBroker? tokenBroker = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cache = cache ?? new InMemoryBhenguDistributedCache();
        _tokenBroker = tokenBroker ?? new CellulantTokenBroker(options!, new Microsoft.Extensions.Logging.Abstractions.NullLogger<CellulantTokenBroker>());

        if (string.IsNullOrWhiteSpace(_options.ServiceCode))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(CellulantOptions.ServiceCode)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(CellulantOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(CellulantOptions.ClientSecret)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var defaultUrl = _options.UseSandbox
                ? "https://online.uat.tingg.africa/"
                : "https://online.tingg.africa/";
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? defaultUrl);
        }
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

            var email = request.Metadata?.GetValueOrDefault("email") ?? "noreply@example.com";
            var name = request.Metadata?.GetValueOrDefault("name") ?? "Customer";
            var msisdn = request.PaymentMethodToken;

            var merchantTransactionId = request.IdempotencyKey
                ?? (string.IsNullOrEmpty(_options.MerchantTransactionId)
                    ? $"tingg-{Guid.NewGuid():N}"
                    : $"{_options.MerchantTransactionId}-{Guid.NewGuid():N}");

            var requestBody = new
            {
                msisdn,
                accountNumber = msisdn,
                payerEmail = email,
                payerClientCode = msisdn,
                payerClientName = name,
                payerAuthEmail = email,
                requestAmount = request.Amount,
                currencyCode = request.Currency.ToUpperInvariant(),
                serviceCode = _options.ServiceCode,
                merchantTransactionId,
                requestDescription = request.Description,
                dueDate = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                languageCode = "en",
                successRedirectUrl = _options.CallbackUrl,
                failRedirectUrl = _options.CallbackUrl,
                paymentWebhookUrl = _options.CallbackUrl,
                countryCode = _options.CountryCode
            };

            var body = await SendAuthorisedAsync(HttpMethod.Post, "checkout/v3/express", requestBody, ct, "ProcessPayment").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<CellulantCheckoutResponse>(body);

            _logger.LogInformation("Cellulant checkout created: {Id} status={Status}",
                response?.CheckoutRequestId, response?.Status);

            var status = MapStatus(response?.Status ?? "pending");
            var pr = new PaymentResponse
            {
                GatewayReference = response?.CheckoutRequestId ?? merchantTransactionId,
                Status = status,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                RedirectUrl = response?.RedirectUrl,
                Message = response?.Status
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

            var externalReference = request.IdempotencyKey ?? $"mula-{Guid.NewGuid():N}";
            var requestBody = new
            {
                sourceServiceCode = _options.ServiceCode,
                destinationMSISDN = request.DestinationToken,
                currencyCode = request.Currency.ToUpperInvariant(),
                amount = request.Amount,
                narration = request.Description,
                countryCode = _options.CountryCode,
                externalReference
            };

            var body = await SendAuthorisedAsync(HttpMethod.Post, "disbursement/v1/initiate", requestBody, ct, "ProcessPayout").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<CellulantPayoutResponse>(body);

            _logger.LogInformation("Cellulant Mula disbursement initiated: {Reference} status={Status}",
                response?.TransactionReference, response?.Status);

            var status = MapStatus(response?.Status ?? "pending");
            var pr = new PayoutResponse
            {
                GatewayReference = response?.TransactionReference ?? string.Empty,
                Status = status,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow
            };

            outcomeTag = status == PaymentStatus.Failed
                ? BhenguPaymentDiagnostics.Outcomes.Declined
                : BhenguPaymentDiagnostics.Outcomes.Success;
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

            var requestBody = new
            {
                transactionId = request.GatewayReference,
                amount = request.Amount,
                reason = request.Reason
            };

            var body = await SendAuthorisedAsync(HttpMethod.Post, "refunds", requestBody, ct, "ProcessRefund").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<CellulantRefundResponse>(body);

            _logger.LogInformation("Cellulant refund processed: {Reference} for transaction {TransactionId}",
                response?.RefundReference, request.GatewayReference);

            var status = MapStatus(response?.Status ?? "pending");
            var pr = new RefundResponse
            {
                GatewayReference = response?.RefundReference ?? request.GatewayReference,
                Amount = request.Amount,
                Status = status,
                ProcessedAt = DateTime.UtcNow,
                Message = response?.Status
            };

            outcomeTag = status == PaymentStatus.Failed
                ? BhenguPaymentDiagnostics.Outcomes.Declined
                : BhenguPaymentDiagnostics.Outcomes.Success;
            await TrySetCachedAsync(request.IdempotencyKey, "refund", pr, ct).ConfigureAwait(false);
            return pr;
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
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            _logger.LogWarning("Cellulant WebhookSecret not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(computedSignature));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cellulant webhook signature verification raised");
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
            var webhookEvent = JsonSerializer.Deserialize<CellulantWebhookEvent>(payload);
            if (webhookEvent is null)
            {
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
                return Task.FromResult<WebhookEvent?>(null);
            }

            _logger.LogInformation("Parsed Cellulant webhook event: {EventType}", webhookEvent.EventType);
            var typed = MapWebhookEvent(webhookEvent);
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return Task.FromResult(typed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Cellulant webhook event");
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private static WebhookEvent? MapWebhookEvent(CellulantWebhookEvent webhookEvent)
    {
        var eventName = webhookEvent.EventType?.ToLowerInvariant();
        var reference = webhookEvent.Data?.CheckoutRequestId ?? webhookEvent.Data?.TransactionId;
        if (string.IsNullOrEmpty(reference)) return null;

        var amount = webhookEvent.Data?.Amount ?? 0m;
        var currency = webhookEvent.Data?.Currency ?? "KES";

        switch (eventName)
        {
            case "payment.success":
            case "checkout.success":
                return new ChargeSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency,
                    CustomerId = webhookEvent.Data?.Msisdn,
                    PaymentMethodToken = webhookEvent.Data?.Msisdn
                };

            case "payment.failed":
            case "checkout.failed":
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

            case "refund.success":
            case "refund.processed":
                return new RefundSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Refunded,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = webhookEvent.Data?.TransactionId ?? reference,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false
                };

            case "disbursement.success":
                return new PayoutCompletedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.PayoutCompleted,
                    PayoutReference = reference,
                    Amount = amount,
                    Currency = currency,
                    DestinationToken = webhookEvent.Data?.Msisdn
                };

            case "disbursement.failed":
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

            case "settlement.completed":
                return new SettlementCompletedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.SettlementCompleted,
                    SettlementReference = reference,
                    NetAmount = amount,
                    Currency = currency
                };

            default:
                var status = eventName switch
                {
                    "payment.success" or "checkout.success" or "disbursement.success" => PaymentStatus.Completed,
                    "payment.failed" or "checkout.failed" or "disbursement.failed" => PaymentStatus.Failed,
                    "refund.success" or "refund.processed" => PaymentStatus.Refunded,
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

    /// <summary>Mint or return a cached Tingg OAuth2 access token. Internal; exposed for sibling providers.</summary>
    internal Task<string> EnsureAccessTokenAsync(CancellationToken ct) =>
        _tokenBroker.EnsureAccessTokenAsync(_httpClient, ct);

    private async Task<string> SendAuthorisedAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var token = await _tokenBroker.EnsureAccessTokenAsync(_httpClient, ct).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Cellulant failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Cellulant {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), responseBody);
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
        return $"cellulant:idem:{operation}:{hash}";
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "success" or "successful" or "completed" or "processed" => PaymentStatus.Completed,
        "pending" or "processing" or "initiated" => PaymentStatus.Pending,
        "failed" or "rejected" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Cellulant API response shapes (internal) ===

    private sealed class CellulantCheckoutResponse
    {
        [JsonPropertyName("checkoutRequestId")] public string? CheckoutRequestId { get; set; }
        [JsonPropertyName("redirectUrl")] public string? RedirectUrl { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class CellulantPayoutResponse
    {
        [JsonPropertyName("transactionReference")] public string? TransactionReference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class CellulantRefundResponse
    {
        [JsonPropertyName("refundReference")] public string? RefundReference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class CellulantWebhookEvent
    {
        [JsonPropertyName("eventType")] public string? EventType { get; set; }
        [JsonPropertyName("data")] public CellulantWebhookData? Data { get; set; }
    }

    private sealed class CellulantWebhookData
    {
        [JsonPropertyName("checkoutRequestId")] public string? CheckoutRequestId { get; set; }
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("msisdn")] public string? Msisdn { get; set; }
    }
}
