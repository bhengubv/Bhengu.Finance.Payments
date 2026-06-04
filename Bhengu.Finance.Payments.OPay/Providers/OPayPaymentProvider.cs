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
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Security;
using Bhengu.Finance.Payments.OPay.Configuration;
using Bhengu.Finance.Payments.OPay.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.OPay.Providers;

/// <summary>
/// OPay (Nigeria/Egypt/Pakistan) payment gateway provider. Wraps the OPay International
/// cashier, refund and payout REST APIs. Requests are HMAC-SHA512 signed with the merchant
/// SecretKey and the signature is carried in the <c>Authorization</c> header.
/// </summary>
public sealed class OPayPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private const string ProductionBaseUrl = "https://liveapi.opaycheckout.com";
    private const string SandboxBaseUrl = "https://sandboxapi.opaycheckout.com";

    private readonly HttpClient _httpClient;
    private readonly OPayOptions _options;
    private readonly OPayIdempotencyCache? _idempotency;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.OPay;

    /// <inheritdoc />
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.BankTransfer |
        ProviderCapabilities.Tokenisation |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public OPayPaymentProvider(
        HttpClient httpClient,
        IOptions<OPayOptions> options,
        ILogger<OPayPaymentProvider> logger,
        OPayIdempotencyCache? idempotency = null)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency;

        if (string.IsNullOrWhiteSpace(_options.PublicKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OPayOptions.PublicKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OPayOptions.SecretKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OPayOptions.MerchantId)} is required");

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
            ? RunChargeAsync(request.Currency, () => ProcessPaymentCoreAsync(request, ct), ct)
            : _idempotency.GetOrAddAsync(request.IdempotencyKey, "charge",
                () => RunChargeAsync(request.Currency, () => ProcessPaymentCoreAsync(request, ct), ct), ct);
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
        var reference = request.Metadata?.GetValueOrDefault("reference") ?? request.IdempotencyKey ?? $"opay-{Guid.NewGuid():N}";
        var userId = request.Metadata?.GetValueOrDefault("userId") ?? "anonymous";
        var userEmail = request.Metadata?.GetValueOrDefault("userEmail") ?? "noreply@bhengu.example";
        var userMobile = request.Metadata?.GetValueOrDefault("userMobile") ?? string.Empty;
        var userName = request.Metadata?.GetValueOrDefault("userName") ?? "Bhengu Customer";

        var amountTotal = (long)(request.Amount * 100);
        var requestBody = new
        {
            publicKey = _options.PublicKey,
            country = _options.Country,
            reference,
            amount = new { total = amountTotal, currency = request.Currency.ToUpperInvariant() },
            returnUrl = _options.ReturnUrl,
            callbackUrl = _options.CallbackUrl,
            expireAt = 30,
            sn = _options.MerchantId,
            productList = new[]
            {
                new
                {
                    name = request.Description,
                    description = request.Description,
                    quantity = 1,
                    price = amountTotal,
                    currency = request.Currency.ToUpperInvariant()
                }
            },
            userInfo = new { userId, userEmail, userMobile, userName },
            payMethod = request.PaymentMethodToken
        };

        var body = await SendAsync(HttpMethod.Post, "api/v1/international/cashier/create",
            requestBody, ct, "ProcessPayment").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<OPayResponse<OPayCashierData>>(body);

        Logger.LogInformation("OPay cashier created: {OrderNo} code={Code}",
            resp?.Data?.OrderNo, resp?.Code);

        var status = MapResponseCode(resp?.Code, resp?.Data?.Status);
        return new PaymentResponse
        {
            GatewayReference = resp?.Data?.OrderNo ?? reference,
            Status = status,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = resp?.Data?.CashierUrl,
            Message = resp?.Message
        };
    }

    /// <inheritdoc />
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency is null
            ? RunRefundAsync(request.GatewayReference, () => ProcessRefundCoreAsync(request, ct), ct)
            : _idempotency.GetOrAddAsync(request.IdempotencyKey, "refund",
                () => RunRefundAsync(request.GatewayReference, () => ProcessRefundCoreAsync(request, ct), ct), ct);
    }

    private async Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
        var amount = (long)(request.Amount * 100);
        var requestBody = new
        {
            publicKey = _options.PublicKey,
            country = _options.Country,
            reference = request.IdempotencyKey ?? $"refund-{Guid.NewGuid():N}",
            orderNo = request.GatewayReference,
            refundAmount = new { total = amount, currency = "NGN" },
            reason = request.Reason,
            callbackUrl = _options.CallbackUrl
        };

        var body = await SendAsync(HttpMethod.Post, "api/v1/international/refund/create",
            requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<OPayResponse<OPayRefundData>>(body);

        Logger.LogInformation("OPay refund created: {RefundId} for order {OrderNo}",
            resp?.Data?.RefundId, request.GatewayReference);

        var status = MapResponseCode(resp?.Code, resp?.Data?.Status);
        return new RefundResponse
        {
            GatewayReference = resp?.Data?.RefundId ?? string.Empty,
            Amount = request.Amount,
            Status = status,
            ProcessedAt = DateTime.UtcNow,
            Message = resp?.Message
        };
    }

    /// <inheritdoc />
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency is null
            ? RunPayoutAsync(request.Currency, () => ProcessPayoutCoreAsync(request, ct), ct)
            : _idempotency.GetOrAddAsync(request.IdempotencyKey, "payout",
                () => RunPayoutAsync(request.Currency, () => ProcessPayoutCoreAsync(request, ct), ct), ct);
    }

    private async Task<PayoutResponse> ProcessPayoutCoreAsync(PayoutRequest request, CancellationToken ct)
    {
        var amountTotal = (long)(request.Amount * 100);
        var requestBody = new
        {
            publicKey = _options.PublicKey,
            country = _options.Country,
            reference = request.IdempotencyKey ?? $"payout-{Guid.NewGuid():N}",
            amount = new { total = amountTotal, currency = request.Currency.ToUpperInvariant() },
            reason = request.Description,
            receiver = new { receiverId = request.DestinationToken },
            callbackUrl = _options.CallbackUrl
        };

        var body = await SendAsync(HttpMethod.Post, "api/v1/international/payout/create",
            requestBody, ct, "ProcessPayout").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<OPayResponse<OPayPayoutData>>(body);

        Logger.LogInformation("OPay payout created: {OrderNo} code={Code}",
            resp?.Data?.OrderNo, resp?.Code);

        var status = MapResponseCode(resp?.Code, resp?.Data?.Status);
        return new PayoutResponse
        {
            GatewayReference = resp?.Data?.OrderNo ?? string.Empty,
            Status = status,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    /// <inheritdoc />
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        return RunWebhookVerify(() =>
        {
            if (string.IsNullOrWhiteSpace(_options.SecretKey))
            {
                Logger.LogWarning("OPay SecretKey not configured — signature verification cannot succeed.");
                return false;
            }

            // OPay typically prefixes "Bearer " on the Authorization webhook header — strip if present.
            var normalised = signature.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? signature["Bearer ".Length..]
                : signature;

            return SignatureHelpers.VerifyHmacSha512(payload, normalised, _options.SecretKey);
        });
    }

    /// <inheritdoc />
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        return RunOperationAsync<WebhookEvent?>("parse_webhook", () =>
        {
            try
            {
                var evt = JsonSerializer.Deserialize<OPayWebhookEvent>(payload);
                if (evt is null) return Task.FromResult<WebhookEvent?>(null);

                Logger.LogInformation("Parsed OPay webhook event: {EventType}", evt.Type);
                var typed = MapWebhookEvent(evt);
                return Task.FromResult(typed);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to parse OPay webhook event");
                return Task.FromResult<WebhookEvent?>(null);
            }
        }, ct);
    }

    private static WebhookEvent? MapWebhookEvent(OPayWebhookEvent webhookEvent)
    {
        var eventType = webhookEvent.Type?.ToLowerInvariant() ?? string.Empty;
        var data = webhookEvent.Payload;
        var rawReference = data?.Reference ?? data?.OrderNo;
        if (string.IsNullOrEmpty(rawReference)) return null;

        var amount = (data?.Amount?.Total ?? 0L) / 100m;
        var currency = data?.Amount?.Currency ?? "NGN";

        switch (eventType)
        {
            case "transaction.success":
            case "payment.success":
            case "transaction.completed":
                return new ChargeSucceededEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.Type,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency,
                    CustomerId = data?.UserId,
                    PaymentMethodToken = data?.PayMethod
                };

            case "transaction.failed":
            case "payment.failed":
                return new ChargeFailedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.Type,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data?.Status,
                    FailureMessage = data?.FailureReason
                };

            case "transaction.pending":
            case "payment.pending":
                return new ChargePendingEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Pending,
                    EventType = webhookEvent.Type,
                    Category = WebhookEventCategory.ChargePending,
                    Amount = amount,
                    Currency = currency
                };

            case "refund.success":
            case "refund.completed":
                return new RefundSucceededEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Refunded,
                    EventType = webhookEvent.Type,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = data?.RefundId ?? rawReference,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false
                };

            case "refund.failed":
                return new RefundFailedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.Type,
                    Category = WebhookEventCategory.RefundFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data?.Status,
                    FailureMessage = data?.FailureReason
                };

            case "payout.success":
            case "transfer.success":
                return new PayoutCompletedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.Type,
                    Category = WebhookEventCategory.PayoutCompleted,
                    PayoutReference = rawReference,
                    Amount = amount,
                    Currency = currency,
                    DestinationToken = data?.ReceiverId
                };

            case "payout.failed":
            case "transfer.failed":
                return new PayoutFailedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.Type,
                    Category = WebhookEventCategory.PayoutFailed,
                    PayoutReference = rawReference,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data?.Status,
                    FailureMessage = data?.FailureReason
                };

            case "settlement.success":
            case "settlement.completed":
                return new SettlementCompletedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.Type,
                    Category = WebhookEventCategory.SettlementCompleted,
                    SettlementReference = rawReference,
                    NetAmount = amount,
                    Currency = currency
                };

            default:
                // Unrecognised but verified event — surface as Category=Unknown so consumers can log
                // and act on new upstream types even before the SDK is taught to classify them.
                return new WebhookEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Pending,
                    EventType = webhookEvent.Type,
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

        // HMAC-SHA512 of the request body using SecretKey, lowercase hex, sent as Authorization: Bearer <sig>
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(_options.SecretKey));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", signature);
        req.Headers.TryAddWithoutValidation("MerchantId", _options.MerchantId);

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("OPay {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapResponseCode(string? code, string? status)
    {
        // OPay envelope code "00000" means success; sub-status carries lifecycle detail.
        if (string.Equals(code, "00000", StringComparison.Ordinal))
        {
            return status?.ToLowerInvariant() switch
            {
                "success" or "successful" => PaymentStatus.Completed,
                "initial" or "pending" or "processing" => PaymentStatus.Pending,
                "failed" or "fail" => PaymentStatus.Failed,
                "close" or "closed" or "cancelled" or "canceled" => PaymentStatus.Cancelled,
                "refunded" => PaymentStatus.Refunded,
                _ => PaymentStatus.Pending
            };
        }
        return PaymentStatus.Failed;
    }

    // === OPay API response shapes (internal) ===

    private sealed class OPayResponse<T> where T : class
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public T? Data { get; set; }
    }

    private sealed class OPayCashierData
    {
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("orderNo")] public string? OrderNo { get; set; }
        [JsonPropertyName("cashierUrl")] public string? CashierUrl { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public OPayAmount? Amount { get; set; }
    }

    private sealed class OPayRefundData
    {
        [JsonPropertyName("refundId")] public string? RefundId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class OPayPayoutData
    {
        [JsonPropertyName("orderNo")] public string? OrderNo { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class OPayAmount
    {
        [JsonPropertyName("total")] public long Total { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }

    private sealed class OPayWebhookEvent
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("payload")] public OPayWebhookPayload? Payload { get; set; }
    }

    private sealed class OPayWebhookPayload
    {
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("orderNo")] public string? OrderNo { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public OPayAmount? Amount { get; set; }
        [JsonPropertyName("userId")] public string? UserId { get; set; }
        [JsonPropertyName("payMethod")] public string? PayMethod { get; set; }
        [JsonPropertyName("refundId")] public string? RefundId { get; set; }
        [JsonPropertyName("receiverId")] public string? ReceiverId { get; set; }
        [JsonPropertyName("failureReason")] public string? FailureReason { get; set; }
    }
}
