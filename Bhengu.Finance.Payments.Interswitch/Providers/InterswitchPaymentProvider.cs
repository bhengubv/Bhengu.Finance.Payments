// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Interswitch.Configuration;
using Bhengu.Finance.Payments.Interswitch.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Interswitch.Providers;

/// <summary>
/// Interswitch (Nigeria/Africa) payment gateway provider. Wraps the Quickteller and Disbursement
/// REST APIs over Interswitch's OAuth2 Passport endpoint. Supports payments, refunds, and
/// disbursements (via <see cref="IPayoutProvider"/>).
/// </summary>
public sealed class InterswitchPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private const string ProductionBaseUrl = "https://passport.interswitchng.com";
    private const string SandboxBaseUrl = "https://qa.interswitchng.com";

    private readonly HttpClient _httpClient;
    private readonly InterswitchOptions _options;
    private readonly ILogger<InterswitchPaymentProvider> _logger;
    private readonly InterswitchIdempotencyCache? _idempotency;

    private string? _cachedAccessToken;
    private DateTime _accessTokenExpiresAtUtc = DateTime.MinValue;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Interswitch;

    /// <inheritdoc />
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.Cards |
        ProviderCapabilities.BankTransfer |
        ProviderCapabilities.Tokenisation |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>
    /// Construct the provider. <paramref name="idempotency"/> is optional; when supplied
    /// (default registration via DI) caller-supplied <c>IdempotencyKey</c> values dedupe across
    /// payments / refunds / payouts.
    /// </summary>
    public InterswitchPaymentProvider(
        HttpClient httpClient,
        IOptions<InterswitchOptions> options,
        ILogger<InterswitchPaymentProvider> logger,
        InterswitchIdempotencyCache? idempotency = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _idempotency = idempotency;

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(InterswitchOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(InterswitchOptions.ClientSecret)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var baseUrl = _options.UseSandbox
                ? _options.SandboxUrl ?? SandboxBaseUrl
                : _options.BaseUrl ?? ProductionBaseUrl;
            if (!baseUrl.EndsWith('/')) baseUrl += "/";
            _httpClient.BaseAddress = new Uri(baseUrl);
        }
    }

    /// <inheritdoc />
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency is null
            ? ProcessPaymentCoreAsync(request, ct)
            : _idempotency.GetOrAddAsync(request.IdempotencyKey, "charge",
                () => ProcessPaymentCoreAsync(request, ct), ct);
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
        using var activity = BhenguPaymentDiagnostics.StartChargeActivity(ProviderName, request.Currency);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outcome = BhenguPaymentDiagnostics.Outcomes.Pending;
        try
        {
            var amountInKobo = (long)(request.Amount * 100);
            var requestRef = request.Metadata?.GetValueOrDefault("requestReference")
                ?? request.IdempotencyKey
                ?? $"isw-{Guid.NewGuid():N}";
            var customerEmail = request.Metadata?.GetValueOrDefault("customerEmail") ?? "noreply@bhengu.example";
            var customerId = request.Metadata?.GetValueOrDefault("customerId") ?? "anonymous";
            var customerMobile = request.Metadata?.GetValueOrDefault("mobileNo") ?? string.Empty;

            var requestBody = new
            {
                customer = new { id = customerId, mobileNo = customerMobile },
                paymentCode = _options.ProductId,
                customerEmail,
                amount = amountInKobo,
                currency = request.Currency.ToUpperInvariant(),
                transferCode = request.PaymentMethodToken,
                requestReference = requestRef
            };

            var body = await SendAsync(HttpMethod.Post, "api/v2/quickteller/payments/advices",
                requestBody, ct, "ProcessPayment").ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<InterswitchAdviceResponse>(body);

            _logger.LogInformation("Interswitch advice created: {Ref} status={Status}",
                resp?.TransactionRef ?? requestRef, resp?.ResponseCode);

            var status = MapResponseCode(resp?.ResponseCode);
            outcome = status switch
            {
                PaymentStatus.Completed => BhenguPaymentDiagnostics.Outcomes.Success,
                PaymentStatus.Pending => BhenguPaymentDiagnostics.Outcomes.Pending,
                PaymentStatus.Failed => BhenguPaymentDiagnostics.Outcomes.Declined,
                _ => BhenguPaymentDiagnostics.Outcomes.Pending
            };

            return new PaymentResponse
            {
                GatewayReference = resp?.TransactionRef ?? requestRef,
                Status = status,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                Message = resp?.ResponseDescription
            };
        }
        catch (PaymentDeclinedException) { outcome = BhenguPaymentDiagnostics.Outcomes.Declined; throw; }
        catch (ProviderRateLimitException) { outcome = BhenguPaymentDiagnostics.Outcomes.RateLimited; throw; }
        catch (ProviderUnavailableException) { outcome = BhenguPaymentDiagnostics.Outcomes.Unavailable; throw; }
        catch (Exception) { outcome = BhenguPaymentDiagnostics.Outcomes.Error; throw; }
        finally
        {
            activity?.SetOutcome(outcome);
            sw.Stop();
            BhenguPaymentDiagnostics.ChargesTotal.Add(1, new KeyValuePair<string, object?>("provider", ProviderName), new KeyValuePair<string, object?>("outcome", outcome));
            BhenguPaymentDiagnostics.ChargeDurationMs.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("provider", ProviderName), new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    /// <inheritdoc />
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency is null
            ? ProcessRefundCoreAsync(request, ct)
            : _idempotency.GetOrAddAsync(request.IdempotencyKey, "refund",
                () => ProcessRefundCoreAsync(request, ct), ct);
    }

    private async Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
        using var activity = BhenguPaymentDiagnostics.StartRefundActivity(ProviderName, request.GatewayReference);
        var outcome = BhenguPaymentDiagnostics.Outcomes.Pending;
        try
        {
            var amountInKobo = (long)(request.Amount * 100);
            var requestBody = new
            {
                amount = amountInKobo,
                reason = request.Reason
            };

            var path = $"api/v2/quickteller/transactions/{Uri.EscapeDataString(request.GatewayReference)}/refund";
            var body = await SendAsync(HttpMethod.Post, path, requestBody, ct, "ProcessRefund").ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<InterswitchRefundResponse>(body);

            _logger.LogInformation("Interswitch refund created: {RefundRef} for txn {TxnRef}",
                resp?.RefundReference, request.GatewayReference);

            var status = MapResponseCode(resp?.ResponseCode);
            outcome = status switch
            {
                PaymentStatus.Completed or PaymentStatus.Refunded => BhenguPaymentDiagnostics.Outcomes.Success,
                PaymentStatus.Pending => BhenguPaymentDiagnostics.Outcomes.Pending,
                _ => BhenguPaymentDiagnostics.Outcomes.Declined
            };

            return new RefundResponse
            {
                GatewayReference = resp?.RefundReference ?? string.Empty,
                Amount = request.Amount,
                Status = status,
                ProcessedAt = DateTime.UtcNow,
                Message = resp?.ResponseDescription
            };
        }
        catch (PaymentDeclinedException) { outcome = BhenguPaymentDiagnostics.Outcomes.Declined; throw; }
        catch (ProviderRateLimitException) { outcome = BhenguPaymentDiagnostics.Outcomes.RateLimited; throw; }
        catch (ProviderUnavailableException) { outcome = BhenguPaymentDiagnostics.Outcomes.Unavailable; throw; }
        catch (Exception) { outcome = BhenguPaymentDiagnostics.Outcomes.Error; throw; }
        finally
        {
            activity?.SetOutcome(outcome);
            BhenguPaymentDiagnostics.RefundsTotal.Add(1, new KeyValuePair<string, object?>("provider", ProviderName), new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    /// <inheritdoc />
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency is null
            ? ProcessPayoutCoreAsync(request, ct)
            : _idempotency.GetOrAddAsync(request.IdempotencyKey, "payout",
                () => ProcessPayoutCoreAsync(request, ct), ct);
    }

    private async Task<PayoutResponse> ProcessPayoutCoreAsync(PayoutRequest request, CancellationToken ct)
    {
        using var activity = BhenguPaymentDiagnostics.StartPayoutActivity(ProviderName, request.Currency);
        var outcome = BhenguPaymentDiagnostics.Outcomes.Pending;
        try
        {
            var amountInKobo = (long)(request.Amount * 100);
            // DestinationToken format: "<bankCode>:<accountNumber>" or just "<accountNumber>".
            string bankCode = string.Empty;
            string accountNumber = request.DestinationToken;
            var sep = request.DestinationToken.IndexOf(':');
            if (sep > 0)
            {
                bankCode = request.DestinationToken[..sep];
                accountNumber = request.DestinationToken[(sep + 1)..];
            }

            var requestBody = new
            {
                amount = amountInKobo,
                beneficiaryAccountNumber = accountNumber,
                beneficiaryBankCode = bankCode,
                narration = request.Description,
                transactionRef = request.IdempotencyKey ?? $"disb-{Guid.NewGuid():N}",
                currencyCode = request.Currency.ToUpperInvariant()
            };

            var body = await SendAsync(HttpMethod.Post, "api/v2/disbursements/transactions",
                requestBody, ct, "ProcessPayout").ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<InterswitchDisbursementResponse>(body);

            _logger.LogInformation("Interswitch disbursement created: {Ref} status={Status}",
                resp?.TransactionRef, resp?.ResponseCode);

            var status = MapResponseCode(resp?.ResponseCode);
            outcome = status switch
            {
                PaymentStatus.Completed => BhenguPaymentDiagnostics.Outcomes.Success,
                PaymentStatus.Pending => BhenguPaymentDiagnostics.Outcomes.Pending,
                _ => BhenguPaymentDiagnostics.Outcomes.Declined
            };

            return new PayoutResponse
            {
                GatewayReference = resp?.TransactionRef ?? string.Empty,
                Status = status,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow
            };
        }
        catch (PaymentDeclinedException) { outcome = BhenguPaymentDiagnostics.Outcomes.Declined; throw; }
        catch (ProviderRateLimitException) { outcome = BhenguPaymentDiagnostics.Outcomes.RateLimited; throw; }
        catch (ProviderUnavailableException) { outcome = BhenguPaymentDiagnostics.Outcomes.Unavailable; throw; }
        catch (Exception) { outcome = BhenguPaymentDiagnostics.Outcomes.Error; throw; }
        finally
        {
            activity?.SetOutcome(outcome);
            BhenguPaymentDiagnostics.PayoutsTotal.Add(1, new KeyValuePair<string, object?>("provider", ProviderName), new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    /// <inheritdoc />
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        var valid = false;
        try
        {
            if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
            {
                _logger.LogWarning("Interswitch WebhookSecret not configured — signature verification cannot succeed.");
                return false;
            }

            using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(_options.WebhookSecret));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();

            valid = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(computedSignature));
            return valid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Interswitch webhook signature verification raised");
            return false;
        }
        finally
        {
            BhenguPaymentDiagnostics.WebhookVerificationsTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("valid", valid));
        }
    }

    /// <inheritdoc />
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        using var activity = BhenguPaymentDiagnostics.StartWebhookActivity(ProviderName);
        try
        {
            var evt = JsonSerializer.Deserialize<InterswitchWebhookEvent>(payload);
            if (evt is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Interswitch webhook event: {EventType}", evt.EventType);

            var typed = MapWebhookEvent(evt);
            return Task.FromResult(typed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Interswitch webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private static WebhookEvent? MapWebhookEvent(InterswitchWebhookEvent webhookEvent)
    {
        var eventType = webhookEvent.EventType?.ToLowerInvariant() ?? string.Empty;
        var data = webhookEvent.Data;
        var rawReference = data?.TransactionRef;
        if (string.IsNullOrEmpty(rawReference)) return null;

        var amount = (data?.Amount ?? 0L) / 100m;
        var currency = data?.Currency ?? "NGN";

        switch (eventType)
        {
            case "payment.successful":
            case "transaction.successful":
            case "charge.success":
                return new ChargeSucceededEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency,
                    CustomerId = data?.CustomerId,
                    PaymentMethodToken = data?.PaymentMethod
                };

            case "payment.failed":
            case "transaction.failed":
            case "charge.failed":
                return new ChargeFailedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data?.Status,
                    FailureMessage = data?.ResponseDescription
                };

            case "payment.pending":
            case "transaction.pending":
                return new ChargePendingEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Pending,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.ChargePending,
                    Amount = amount,
                    Currency = currency
                };

            case "refund.successful":
            case "refund.processed":
                return new RefundSucceededEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Refunded,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = data?.RefundReference ?? rawReference,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false
                };

            case "refund.failed":
                return new RefundFailedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.RefundFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data?.Status,
                    FailureMessage = data?.ResponseDescription
                };

            case "disbursement.successful":
            case "transfer.successful":
            case "payout.successful":
                return new PayoutCompletedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.PayoutCompleted,
                    PayoutReference = rawReference,
                    Amount = amount,
                    Currency = currency,
                    DestinationToken = data?.BeneficiaryAccount
                };

            case "disbursement.failed":
            case "transfer.failed":
            case "payout.failed":
                return new PayoutFailedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.PayoutFailed,
                    PayoutReference = rawReference,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data?.Status,
                    FailureMessage = data?.ResponseDescription
                };

            case "settlement.completed":
            case "settlement.processed":
                return new SettlementCompletedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.SettlementCompleted,
                    SettlementReference = rawReference,
                    NetAmount = amount,
                    Currency = currency
                };

            default:
                return null;
        }
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Guid.NewGuid().ToString("N");
        var resourceUrl = path.StartsWith('/') ? path : "/" + path;
        var signature = ComputeRequestSignature(method.Method, resourceUrl, timestampMs, nonce);

        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("Signature", signature);
        req.Headers.TryAddWithoutValidation("SignatureMethod", "SHA-512");
        req.Headers.TryAddWithoutValidation("Timestamp", timestampMs);
        req.Headers.TryAddWithoutValidation("Nonce", nonce);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Interswitch failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Interswitch {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private async Task<string> EnsureAccessTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_cachedAccessToken) && DateTime.UtcNow < _accessTokenExpiresAtUtc.AddSeconds(-30))
            return _cachedAccessToken;

        using var req = new HttpRequestMessage(HttpMethod.Post, "passport/oauth/token");
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("scope", "profile")
        });

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "Interswitch token endpoint unreachable", ex);
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Interswitch OAuth2 token failed: {StatusCode} {Body}", response.StatusCode, body);
            throw new ProviderUnavailableException(ProviderName, $"OAuth2 token HTTP {(int)response.StatusCode}: {body}");
        }

        var token = JsonSerializer.Deserialize<InterswitchTokenResponse>(body);
        if (token is null || string.IsNullOrEmpty(token.AccessToken))
            throw new ProviderUnavailableException(ProviderName, "Interswitch OAuth2 token response missing access_token");

        _cachedAccessToken = token.AccessToken;
        _accessTokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 3600);
        return _cachedAccessToken;
    }

    private string ComputeRequestSignature(string method, string resource, string timestampMs, string nonce)
    {
        // Interswitch documented format: SHA-512 hex of clientId+method+resource+timestamp+nonce+secretKey
        var raw = _options.ClientId + method + resource + timestampMs + nonce + _options.ClientSecret;
        return Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static PaymentStatus MapResponseCode(string? code) => code switch
    {
        "00" or "0" or "SUCCESS" or "Success" or "successful" => PaymentStatus.Completed,
        "09" or "PENDING" or "pending" or "processing" => PaymentStatus.Pending,
        "10" or "REFUNDED" or "refunded" => PaymentStatus.Refunded,
        null or "" => PaymentStatus.Pending,
        _ => PaymentStatus.Failed
    };

    // === Interswitch API response shapes (internal) ===

    private sealed class InterswitchTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class InterswitchAdviceResponse
    {
        [JsonPropertyName("transactionRef")] public string? TransactionRef { get; set; }
        [JsonPropertyName("responseCode")] public string? ResponseCode { get; set; }
        [JsonPropertyName("responseDescription")] public string? ResponseDescription { get; set; }
    }

    private sealed class InterswitchRefundResponse
    {
        [JsonPropertyName("refundReference")] public string? RefundReference { get; set; }
        [JsonPropertyName("responseCode")] public string? ResponseCode { get; set; }
        [JsonPropertyName("responseDescription")] public string? ResponseDescription { get; set; }
    }

    private sealed class InterswitchDisbursementResponse
    {
        [JsonPropertyName("transactionRef")] public string? TransactionRef { get; set; }
        [JsonPropertyName("responseCode")] public string? ResponseCode { get; set; }
        [JsonPropertyName("responseDescription")] public string? ResponseDescription { get; set; }
    }

    private sealed class InterswitchWebhookEvent
    {
        [JsonPropertyName("eventType")] public string? EventType { get; set; }
        [JsonPropertyName("data")] public InterswitchWebhookData? Data { get; set; }
    }

    private sealed class InterswitchWebhookData
    {
        [JsonPropertyName("transactionRef")] public string? TransactionRef { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("responseDescription")] public string? ResponseDescription { get; set; }
        [JsonPropertyName("customerId")] public string? CustomerId { get; set; }
        [JsonPropertyName("paymentMethod")] public string? PaymentMethod { get; set; }
        [JsonPropertyName("refundReference")] public string? RefundReference { get; set; }
        [JsonPropertyName("beneficiaryAccountNumber")] public string? BeneficiaryAccount { get; set; }
    }
}
