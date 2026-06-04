// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
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
using Bhengu.Finance.Payments.EcoCash.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.EcoCash.Providers;

/// <summary>
/// EcoCash (Zimbabwe) mobile-money gateway provider. Wraps the EcoCash Developers v2 REST API.
/// Implements C2B instant charges, refunds, and merchant-to-subscriber (B2C) payouts.
/// Webhooks are POSTed to the configured <c>NotifyUrl</c>; the provider supplies no HMAC, so
/// signature verification relies on the secret-URL convention and clientCorrelator matching.
/// </summary>
public sealed class EcoCashPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly EcoCashOptions _options;
    private readonly ILogger<EcoCashPaymentProvider> _logger;
    private readonly IBhenguDistributedCache _cache;
    private static readonly TimeSpan s_idempotencyTtl = TimeSpan.FromHours(24);

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.EcoCash;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public EcoCashPaymentProvider(
        HttpClient httpClient,
        IOptions<EcoCashOptions> options,
        ILogger<EcoCashPaymentProvider> logger,
        IBhenguDistributedCache? cache = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cache = cache ?? new InMemoryBhenguDistributedCache();

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(EcoCashOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantCode))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(EcoCashOptions.MerchantCode)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var defaultUrl = _options.UseSandbox
                ? "https://developers.ecocash.co.zw/sandbox/"
                : "https://developers.ecocash.co.zw/";
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? defaultUrl);
        }

        _httpClient.DefaultRequestHeaders.Remove("X-Api-Key");
        _httpClient.DefaultRequestHeaders.Add("X-Api-Key", _options.ApiKey);

        if (!string.IsNullOrEmpty(_options.Username))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
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

            var clientCorrelator = request.IdempotencyKey ?? $"ecocash-{Guid.NewGuid():N}";
            var requestBody = BuildC2BBody(request, clientCorrelator, tranType: "MER");

            var body = await SendAsync(HttpMethod.Post, "api/v2/payment/instant/c2b/live", requestBody, ct, "ProcessPayment").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<EcoCashTransactionResponse>(body);

            _logger.LogInformation("EcoCash C2B created: {Correlator} status={Status}",
                response?.ClientCorrelator ?? clientCorrelator, response?.TransactionOperationStatus);

            var status = MapStatus(response?.TransactionOperationStatus ?? "pending");
            var pr = new PaymentResponse
            {
                GatewayReference = response?.EcocashReference ?? response?.ClientCorrelator ?? clientCorrelator,
                Status = status,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                Message = response?.TransactionOperationStatus
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

            var clientCorrelator = request.IdempotencyKey ?? $"ecocash-refund-{Guid.NewGuid():N}";
            var refundRequest = new PaymentRequest
            {
                PaymentMethodToken = request.GatewayReference,
                Amount = request.Amount,
                Currency = "USD",
                Description = request.Reason
            };

            var requestBody = BuildC2BBody(refundRequest, clientCorrelator, tranType: "REFUND",
                originalReference: request.GatewayReference);

            var body = await SendAsync(HttpMethod.Post, "api/v2/payment/instant/refund", requestBody, ct, "ProcessRefund").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<EcoCashTransactionResponse>(body);

            _logger.LogInformation("EcoCash refund initiated: {Correlator} for {Original}",
                clientCorrelator, request.GatewayReference);

            var status = MapStatus(response?.TransactionOperationStatus ?? "pending");
            var rr = new RefundResponse
            {
                GatewayReference = response?.EcocashReference ?? clientCorrelator,
                Amount = request.Amount,
                Status = status,
                ProcessedAt = DateTime.UtcNow,
                Message = response?.TransactionOperationStatus
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

            var clientCorrelator = request.IdempotencyKey ?? $"ecocash-payout-{Guid.NewGuid():N}";
            var requestBody = new
            {
                clientCorrelator,
                notifyUrl = _options.NotifyUrl,
                referenceCode = clientCorrelator,
                tranType = "DIS",
                endUserId = request.DestinationToken,
                remarks = request.Description,
                transactionOperationStatus = "Charged",
                amount = new
                {
                    charging = new
                    {
                        amount = request.Amount,
                        currency = request.Currency.ToUpperInvariant()
                    }
                },
                merchantCode = _options.MerchantCode,
                merchantPin = _options.MerchantPin,
                merchantNumber = _options.MerchantNumber
            };

            var body = await SendAsync(HttpMethod.Post, "api/v2/payment/instant/merchanttosubscriber", requestBody, ct, "ProcessPayout").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<EcoCashTransactionResponse>(body);

            _logger.LogInformation("EcoCash B2C disbursement initiated: {Correlator} status={Status}",
                clientCorrelator, response?.TransactionOperationStatus);

            var status = MapStatus(response?.TransactionOperationStatus ?? "pending");
            var pr = new PayoutResponse
            {
                GatewayReference = response?.EcocashReference ?? response?.ClientCorrelator ?? clientCorrelator,
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
        // EcoCash does NOT sign callbacks. Authenticity is established by sending callbacks to a
        // secret URL (NotifyUrl) plus matching the clientCorrelator in the body against the value
        // sent on the original charge. Callers should perform that match in their webhook handler.
        ArgumentException.ThrowIfNullOrEmpty(payload);
        _logger.LogWarning("EcoCash does not sign callbacks — relying on NotifyUrl secrecy and clientCorrelator match instead.");
        return !string.IsNullOrEmpty(signature);
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        using var activity = BhenguPaymentDiagnostics.StartWebhookActivity(ProviderName);
        try
        {
            var response = JsonSerializer.Deserialize<EcoCashTransactionResponse>(payload);
            if (response is null)
            {
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
                return Task.FromResult<WebhookEvent?>(null);
            }

            _logger.LogInformation("Parsed EcoCash webhook: {Status}", response.TransactionOperationStatus);
            var typed = MapWebhookEvent(response);
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return Task.FromResult(typed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse EcoCash webhook event");
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private static WebhookEvent? MapWebhookEvent(EcoCashTransactionResponse response)
    {
        var reference = response.EcocashReference ?? response.ClientCorrelator;
        if (string.IsNullOrEmpty(reference)) return null;

        var status = response.TransactionOperationStatus?.ToLowerInvariant();
        var amount = response.Amount?.Charging?.Amount ?? 0m;
        var currency = response.Amount?.Charging?.Currency ?? "USD";
        var isDisbursement = string.Equals(response.TranType, "DIS", StringComparison.OrdinalIgnoreCase);
        var isRefund = string.Equals(response.TranType, "REFUND", StringComparison.OrdinalIgnoreCase);

        switch (status)
        {
            case "completed":
            case "charged":
            case "success":
                if (isDisbursement)
                    return new PayoutCompletedEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Completed,
                        EventType = response.TransactionOperationStatus,
                        Category = WebhookEventCategory.PayoutCompleted,
                        PayoutReference = reference,
                        Amount = amount,
                        Currency = currency,
                        DestinationToken = response.EndUserId
                    };
                if (isRefund)
                    return new RefundSucceededEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Refunded,
                        EventType = response.TransactionOperationStatus,
                        Category = WebhookEventCategory.RefundSucceeded,
                        RefundReference = reference,
                        Amount = amount,
                        Currency = currency,
                        IsPartial = false
                    };
                return new ChargeSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = response.TransactionOperationStatus,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency,
                    CustomerId = response.EndUserId,
                    PaymentMethodToken = response.EndUserId
                };

            case "failed":
            case "denied":
                if (isDisbursement)
                    return new PayoutFailedEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Failed,
                        EventType = response.TransactionOperationStatus,
                        Category = WebhookEventCategory.PayoutFailed,
                        PayoutReference = reference,
                        Amount = amount,
                        Currency = currency,
                        FailureCode = response.TransactionOperationStatus,
                        FailureMessage = response.TransactionOperationStatus
                    };
                return new ChargeFailedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Failed,
                    EventType = response.TransactionOperationStatus,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = response.TransactionOperationStatus,
                    FailureMessage = response.TransactionOperationStatus
                };

            case "refunded":
                return new RefundSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Refunded,
                    EventType = response.TransactionOperationStatus,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = reference,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false
                };

            default:
                var legacy = status switch
                {
                    "pending" or "pending subscriber validation" => PaymentStatus.Pending,
                    _ => (PaymentStatus?)null
                };
                if (legacy is null) return null;
                return new WebhookEvent
                {
                    GatewayReference = reference,
                    Status = legacy.Value,
                    EventType = response.TransactionOperationStatus,
                    Category = WebhookEventCategory.Unknown
                };
        }
    }

    private object BuildC2BBody(PaymentRequest request, string clientCorrelator, string tranType, string? originalReference = null)
    {
        return new
        {
            clientCorrelator,
            notifyUrl = _options.NotifyUrl,
            referenceCode = originalReference ?? clientCorrelator,
            tranType,
            endUserId = request.PaymentMethodToken,
            remarks = request.Description,
            transactionOperationStatus = tranType == "REFUND" ? "Refunded" : "Charged",
            amount = new
            {
                charging = new
                {
                    amount = request.Amount,
                    currency = request.Currency.ToUpperInvariant()
                }
            },
            merchantCode = _options.MerchantCode,
            merchantPin = _options.MerchantPin,
            merchantNumber = _options.MerchantNumber
        };
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to EcoCash failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("EcoCash {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
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
        return $"ecocash:idem:{operation}:{hash}";
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "completed" or "charged" or "success" or "successful" => PaymentStatus.Completed,
        "pending" or "pending subscriber validation" or "processing" => PaymentStatus.Pending,
        "failed" or "denied" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === EcoCash API response shapes (internal) ===

    private sealed class EcoCashTransactionResponse
    {
        [JsonPropertyName("clientCorrelator")] public string? ClientCorrelator { get; set; }
        [JsonPropertyName("ecocashReference")] public string? EcocashReference { get; set; }
        [JsonPropertyName("transactionOperationStatus")] public string? TransactionOperationStatus { get; set; }
        [JsonPropertyName("referenceCode")] public string? ReferenceCode { get; set; }
        [JsonPropertyName("endUserId")] public string? EndUserId { get; set; }
        [JsonPropertyName("tranType")] public string? TranType { get; set; }
        [JsonPropertyName("amount")] public EcoCashAmountWrapper? Amount { get; set; }
    }

    private sealed class EcoCashAmountWrapper
    {
        [JsonPropertyName("charging")] public EcoCashChargingAmount? Charging { get; set; }
    }

    private sealed class EcoCashChargingAmount
    {
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }
}
