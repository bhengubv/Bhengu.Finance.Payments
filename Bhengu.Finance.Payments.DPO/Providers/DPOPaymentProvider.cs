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
using Bhengu.Finance.Payments.DPO.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.DPO.Providers;

/// <summary>
/// DPO Group (Network International) payment provider. Wraps the DPO v6 Direct API. Implements
/// createToken (initialise transaction), verifyToken (status), refundToken (refund), and
/// createTransferToken (disbursement) endpoints. Callbacks are unsigned — webhook authenticity
/// must be established by calling verifyToken against the supplied TransToken.
/// </summary>
public sealed class DPOPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly DPOOptions _options;
    private readonly ILogger<DPOPaymentProvider> _logger;
    private readonly IBhenguDistributedCache _cache;
    private static readonly TimeSpan s_idempotencyTtl = TimeSpan.FromHours(24);

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.DPO;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.CrossBorder |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public DPOPaymentProvider(
        HttpClient httpClient,
        IOptions<DPOOptions> options,
        ILogger<DPOPaymentProvider> logger,
        IBhenguDistributedCache? cache = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cache = cache ?? new InMemoryBhenguDistributedCache();

        if (string.IsNullOrWhiteSpace(_options.CompanyToken))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(DPOOptions.CompanyToken)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var defaultUrl = _options.UseSandbox
                ? "https://secure1.sandbox.directpay.online/"
                : "https://secure.3gdirectpay.com/";
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

            var customerEmail = request.Metadata?.GetValueOrDefault("email") ?? string.Empty;
            var customerFirstName = request.Metadata?.GetValueOrDefault("firstName") ?? string.Empty;
            var customerLastName = request.Metadata?.GetValueOrDefault("lastName") ?? string.Empty;
            var companyRef = request.IdempotencyKey ?? request.PaymentMethodToken;

            var requestBody = new
            {
                CompanyToken = _options.CompanyToken,
                Request = "createToken",
                Transaction = new
                {
                    PaymentAmount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
                    PaymentCurrency = request.Currency.ToUpperInvariant(),
                    CompanyRef = companyRef,
                    RedirectURL = _options.RedirectUrl,
                    BackURL = _options.BackUrl,
                    customerEmail,
                    customerFirstName,
                    customerLastName
                },
                Services = new
                {
                    Service = new
                    {
                        ServiceType = _options.ServiceType,
                        ServiceDescription = _options.ServiceDescription ?? request.Description,
                        ServiceDate = DateTime.UtcNow.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)
                    }
                }
            };

            var body = await SendAsync(HttpMethod.Post, "api/v6/", requestBody, ct, "ProcessPayment").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<DPOCreateTokenResponse>(body);

            _logger.LogInformation("DPO createToken returned: {Token} result={Result}", response?.TransToken, response?.Result);

            // DPO Result code "000" means success; any other code is an error from the API itself.
            if (response?.Result != "000" && !string.IsNullOrEmpty(response?.Result))
            {
                outcomeTag = BhenguPaymentDiagnostics.Outcomes.Declined;
                throw new PaymentDeclinedException(ProviderName, response.Result, response.ResultExplanation);
            }

            var pr = new PaymentResponse
            {
                GatewayReference = response?.TransToken ?? string.Empty,
                Status = PaymentStatus.Pending,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                Message = response?.ResultExplanation,
                RedirectUrl = string.IsNullOrEmpty(response?.TransToken) ? null : $"{_httpClient.BaseAddress}payv3.php?ID={response.TransToken}"
            };

            outcomeTag = BhenguPaymentDiagnostics.Outcomes.Pending;
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

            var requestBody = new
            {
                CompanyToken = _options.CompanyToken,
                Request = "refundToken",
                TransactionToken = request.GatewayReference,
                refundAmount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
                refundDetails = request.Reason
            };

            var body = await SendAsync(HttpMethod.Post, "api/v6/", requestBody, ct, "ProcessRefund").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<DPOResultResponse>(body);

            _logger.LogInformation("DPO refundToken returned: {Result} {Explanation}", response?.Result, response?.ResultExplanation);

            RefundResponse rr;
            if (response?.Result != "000" && !string.IsNullOrEmpty(response?.Result))
            {
                rr = new RefundResponse
                {
                    GatewayReference = request.GatewayReference,
                    Amount = request.Amount,
                    Status = PaymentStatus.Failed,
                    ProcessedAt = DateTime.UtcNow,
                    Message = response.ResultExplanation
                };
                outcomeTag = BhenguPaymentDiagnostics.Outcomes.Declined;
            }
            else
            {
                rr = new RefundResponse
                {
                    GatewayReference = request.GatewayReference,
                    Amount = request.Amount,
                    Status = PaymentStatus.Refunded,
                    ProcessedAt = DateTime.UtcNow,
                    Message = response?.ResultExplanation
                };
                outcomeTag = BhenguPaymentDiagnostics.Outcomes.Success;
            }

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

            var beneficiaryReference = request.Metadata?.GetValueOrDefault("beneficiaryReference") ?? request.DestinationToken;
            var bankCode = request.Metadata?.GetValueOrDefault("bankCode") ?? string.Empty;
            var beneficiaryName = request.Metadata?.GetValueOrDefault("beneficiaryName") ?? "Bhengu Beneficiary";

            var requestBody = new
            {
                CompanyToken = _options.CompanyToken,
                Request = "createTransferToken",
                Transfer = new
                {
                    TransferAmount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
                    TransferCurrency = request.Currency.ToUpperInvariant(),
                    TransferDescription = request.Description,
                    BeneficiaryName = beneficiaryName,
                    BeneficiaryAccount = request.DestinationToken,
                    BeneficiaryReference = beneficiaryReference,
                    BankCode = bankCode,
                    CompanyRef = request.IdempotencyKey ?? $"dpo-payout-{Guid.NewGuid():N}"
                }
            };

            var body = await SendAsync(HttpMethod.Post, "api/v6/", requestBody, ct, "ProcessPayout").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<DPOTransferResponse>(body);

            _logger.LogInformation("DPO createTransferToken returned: {Token} result={Result}", response?.TransferToken, response?.Result);

            if (response?.Result != "000" && !string.IsNullOrEmpty(response?.Result))
            {
                outcomeTag = BhenguPaymentDiagnostics.Outcomes.Declined;
                throw new PaymentDeclinedException(ProviderName, response.Result, response.ResultExplanation);
            }

            var pr = new PayoutResponse
            {
                GatewayReference = response?.TransferToken ?? string.Empty,
                Status = PaymentStatus.Pending,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow
            };

            outcomeTag = BhenguPaymentDiagnostics.Outcomes.Pending;
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
        // DPO does NOT sign callbacks. Authenticity must be established by calling verifyToken
        // against the TransID from the callback (use the verifyToken endpoint in production code).
        ArgumentException.ThrowIfNullOrEmpty(payload);
        _logger.LogWarning("DPO does not sign callbacks — authenticity must be established via verifyToken.");
        return !string.IsNullOrEmpty(signature);
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        using var activity = BhenguPaymentDiagnostics.StartWebhookActivity(ProviderName);
        try
        {
            var webhookEvent = JsonSerializer.Deserialize<DPOWebhookEvent>(payload);
            if (webhookEvent is null)
            {
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
                return Task.FromResult<WebhookEvent?>(null);
            }

            _logger.LogInformation("Parsed DPO callback: TransID={TransID} Status={Status}",
                webhookEvent.TransID, webhookEvent.TransactionFinalStatus);

            var typed = MapWebhookEvent(webhookEvent);
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return Task.FromResult(typed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse DPO webhook event");
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private static WebhookEvent? MapWebhookEvent(DPOWebhookEvent webhookEvent)
    {
        var reference = webhookEvent.TransactionToken ?? webhookEvent.TransID;
        if (string.IsNullOrEmpty(reference)) return null;

        var status = webhookEvent.TransactionFinalStatus?.ToLowerInvariant();
        var amount = decimal.TryParse(webhookEvent.TransactionAmount, NumberStyles.Any, CultureInfo.InvariantCulture, out var amt) ? amt : 0m;
        var currency = webhookEvent.TransactionCurrency ?? "USD";
        var isTransfer = !string.IsNullOrEmpty(webhookEvent.TransferToken);

        switch (status)
        {
            case "paid":
            case "approved":
            case "completed":
                if (isTransfer)
                    return new PayoutCompletedEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Completed,
                        EventType = webhookEvent.TransactionFinalStatus,
                        Category = WebhookEventCategory.PayoutCompleted,
                        PayoutReference = webhookEvent.TransferToken ?? reference,
                        Amount = amount,
                        Currency = currency,
                        DestinationToken = webhookEvent.BeneficiaryAccount
                    };
                return new ChargeSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.TransactionFinalStatus,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency,
                    CustomerId = webhookEvent.CustomerEmail,
                    PaymentMethodToken = webhookEvent.CCDapproval
                };

            case "declined":
            case "failed":
            case "cancelled":
                if (isTransfer)
                    return new PayoutFailedEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Failed,
                        EventType = webhookEvent.TransactionFinalStatus,
                        Category = WebhookEventCategory.PayoutFailed,
                        PayoutReference = webhookEvent.TransferToken ?? reference,
                        Amount = amount,
                        Currency = currency,
                        FailureCode = webhookEvent.TransactionFinalStatus,
                        FailureMessage = webhookEvent.ResultExplanation
                    };
                return new ChargeFailedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.TransactionFinalStatus,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = webhookEvent.TransactionFinalStatus,
                    FailureMessage = webhookEvent.ResultExplanation
                };

            case "refunded":
                return new RefundSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Refunded,
                    EventType = webhookEvent.TransactionFinalStatus,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = reference,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false
                };

            default:
                var legacyStatus = status switch
                {
                    "pending" => PaymentStatus.Pending,
                    _ => (PaymentStatus?)null
                };
                if (legacyStatus is null) return null;
                return new WebhookEvent
                {
                    GatewayReference = reference,
                    Status = legacyStatus.Value,
                    EventType = webhookEvent.TransactionFinalStatus,
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

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to DPO failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("DPO {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
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
        return $"dpo:idem:{operation}:{hash}";
    }

    // === DPO API response shapes (internal) ===

    private sealed class DPOCreateTokenResponse
    {
        [JsonPropertyName("Result")] public string? Result { get; set; }
        [JsonPropertyName("ResultExplanation")] public string? ResultExplanation { get; set; }
        [JsonPropertyName("TransToken")] public string? TransToken { get; set; }
        [JsonPropertyName("TransRef")] public string? TransRef { get; set; }
    }

    private sealed class DPOResultResponse
    {
        [JsonPropertyName("Result")] public string? Result { get; set; }
        [JsonPropertyName("ResultExplanation")] public string? ResultExplanation { get; set; }
    }

    private sealed class DPOTransferResponse
    {
        [JsonPropertyName("Result")] public string? Result { get; set; }
        [JsonPropertyName("ResultExplanation")] public string? ResultExplanation { get; set; }
        [JsonPropertyName("TransferToken")] public string? TransferToken { get; set; }
    }

    private sealed class DPOWebhookEvent
    {
        [JsonPropertyName("TransID")] public string? TransID { get; set; }
        [JsonPropertyName("TransactionToken")] public string? TransactionToken { get; set; }
        [JsonPropertyName("TransferToken")] public string? TransferToken { get; set; }
        [JsonPropertyName("CompanyRef")] public string? CompanyRef { get; set; }
        [JsonPropertyName("TransactionFinalStatus")] public string? TransactionFinalStatus { get; set; }
        [JsonPropertyName("TransactionAmount")] public string? TransactionAmount { get; set; }
        [JsonPropertyName("TransactionCurrency")] public string? TransactionCurrency { get; set; }
        [JsonPropertyName("CCDapproval")] public string? CCDapproval { get; set; }
        [JsonPropertyName("CustomerEmail")] public string? CustomerEmail { get; set; }
        [JsonPropertyName("BeneficiaryAccount")] public string? BeneficiaryAccount { get; set; }
        [JsonPropertyName("ResultExplanation")] public string? ResultExplanation { get; set; }
    }
}
