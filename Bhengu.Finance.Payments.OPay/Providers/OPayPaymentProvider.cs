// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Validation;
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
/// OPay (Nigeria/Egypt/Pakistan) payment gateway provider. Wraps the OPay International Cashier
/// (Checkout) REST API: create a hosted-checkout order, query its status, and refund it.
///
/// <para><b>Auth has two modes</b> (see <see cref="Internals.OPaySignature"/>):
/// the hosted Cashier <c>cashier/create</c> call authenticates with
/// <c>Authorization: Bearer {PublicKey}</c> + a <c>MerchantId</c> header
/// (https://documentation.opaycheckout.com/cashier-create); the signed server-to-server APIs
/// (refund, status) authenticate with an HMAC-SHA512 (hex) signature of the alphabetically
/// key-sorted JSON body, carried as <c>Authorization: Bearer {signature}</c> + <c>MerchantId</c>
/// (https://documentation.opaycheckout.com/api-signature).</para>
///
/// <para>Inbound callbacks carry an HMAC-SHA3-512 <c>sha512</c> field over a fixed format string —
/// see <see cref="VerifyWebhookSignature"/> (https://documentation.opaycheckout.com/callback-signature).</para>
/// </summary>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "Wire format built from public documentation; never sandbox-verified.")]
public sealed class OPayPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    // Hosts verified verbatim from the OPay docs' "Request URL" lines:
    //   production: https://liveapi.opaycheckout.com/...  (cashier-create, payment-refund, query-payment-status)
    //   sandbox:    https://testapi.opaycheckout.com/...   (NOT sandboxapi.*)
    private const string ProductionBaseUrl = "https://liveapi.opaycheckout.com";
    private const string SandboxBaseUrl = "https://testapi.opaycheckout.com";

    /// <summary>How the <c>Authorization</c> header is populated for a given OPay endpoint.</summary>
    private enum AuthMode
    {
        /// <summary>cashier/create: <c>Authorization: Bearer {PublicKey}</c>, no body signature.</summary>
        PublicKeyBearer,
        /// <summary>refund/status/etc.: <c>Authorization: Bearer {HMAC-SHA512(sorted body, SecretKey)}</c>.</summary>
        SignedBody
    }

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
        // Cashier create body per https://documentation.opaycheckout.com/cashier-create —
        // amount.total is in the minor unit (kobo/cent); userInfo and product are the documented
        // shapes (a single "product" object, not a "productList"). PublicKey + MerchantId travel in
        // the headers (AuthMode.PublicKeyBearer), so they are NOT repeated in the body.
        var requestBody = new
        {
            country = _options.Country,
            reference,
            amount = new { total = amountTotal, currency = request.Currency.ToUpperInvariant() },
            returnUrl = _options.ReturnUrl,
            callbackUrl = _options.CallbackUrl,
            cancelUrl = _options.ReturnUrl,
            expireAt = 30,
            payMethod = request.PaymentMethodToken,
            userInfo = new { userId, userEmail, userMobile, userName },
            product = new { name = request.Description, description = request.Description }
        };

        var body = await SendAsync(HttpMethod.Post, "api/v1/international/cashier/create",
            requestBody, AuthMode.PublicKeyBearer, ct, "ProcessPayment").ConfigureAwait(false);
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
        // Refund body + path per https://documentation.opaycheckout.com/payment-refund — a SIGNED
        // server API (AuthMode.SignedBody). OPay keys the refund off the merchant's ORIGINAL order
        // reference (originalReference) and uses its own fresh "reference" for the refund itself.
        // refundWay="Original" returns funds to the original instrument where supported (BankAccount /
        // OpayWallet); BankCard/BankTransfer/BankUssd require a "RefundToBankAccount" with receiver
        // bank details — not modelled here, so we request "Original".
        // UNVERIFIED: the SDK stores the OPay orderNo in RefundRequest.GatewayReference, but OPay's
        // refund request matches on the merchant's original reference. Callers must pass the original
        // merchant reference as GatewayReference for the refund to resolve. Left as-is pending a
        // sandbox round-trip.
        var requestBody = new
        {
            country = _options.Country,
            reference = request.IdempotencyKey ?? $"refund-{Guid.NewGuid():N}",
            originalReference = request.GatewayReference,
            amount = new { total = amount, currency = CurrencyForCountry(_options.Country) },
            refundWay = "Original",
            refundReason = request.Reason,
            callbackUrl = _options.CallbackUrl
        };

        var body = await SendAsync(HttpMethod.Post, "api/v1/international/payment/refund/create",
            requestBody, AuthMode.SignedBody, ct, "ProcessRefund").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<OPayResponse<OPayRefundData>>(body);

        Logger.LogInformation("OPay refund created: order={OrderNo} status={Status}",
            resp?.Data?.OrderNo, resp?.Data?.OrderStatus);

        var status = MapResponseCode(resp?.Code, resp?.Data?.OrderStatus);
        return new RefundResponse
        {
            // OPay's refund response echoes the refund's own reference/orderNo rather than a discrete
            // refundId; surface the orderNo (falling back to the refund reference) as the handle.
            GatewayReference = resp?.Data?.OrderNo ?? resp?.Data?.Reference ?? string.Empty,
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
        // UNVERIFIED: OPay's PUBLIC Cashier/Checkout documentation
        // (https://documentation.opaycheckout.com/) covers collection (create / status / refund /
        // close) and callbacks only — it does NOT document a generic merchant payout/disbursement
        // endpoint. The path "api/v1/international/payout/create" and this body shape are unverified
        // and likely belong to a separate (gated) OPay disbursement product. Left as-is rather than
        // invented to a different-but-wrong shape; the request is still HMAC-SHA512 signed per the
        // documented scheme. Treat ProcessPayoutAsync as unsupported until verified against the
        // real disbursement docs / a sandbox key.
        var requestBody = new
        {
            country = _options.Country,
            reference = request.IdempotencyKey ?? $"payout-{Guid.NewGuid():N}",
            amount = new { total = amountTotal, currency = request.Currency.ToUpperInvariant() },
            reason = request.Description,
            receiver = new { receiverId = request.DestinationToken },
            callbackUrl = _options.CallbackUrl
        };

        var body = await SendAsync(HttpMethod.Post, "api/v1/international/payout/create",
            requestBody, AuthMode.SignedBody, ct, "ProcessPayout").ConfigureAwait(false);
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

    /// <summary>
    /// Verify an OPay payment-notification callback.
    ///
    /// <para>OPay does NOT sign the raw body, and the signature is NOT an HTTP header — instead the
    /// callback JSON carries a <c>sha512</c> field that is an <b>HMAC-SHA3-512</b> (hex) of a fixed
    /// "sign content" string built from eight fields of the <c>payload</c> object, keyed on the
    /// merchant Private Key (SecretKey). Source (verbatim):
    /// https://documentation.opaycheckout.com/callback-signature.</para>
    ///
    /// <para>Call this with <paramref name="payload"/> = the raw callback JSON body, and
    /// <paramref name="signature"/> = the callback's <c>sha512</c> value. As a convenience, if
    /// <paramref name="signature"/> is empty/whitespace the <c>sha512</c> field is read from the body.</para>
    ///
    /// <para><b>Runtime note:</b> HMAC-SHA3-512 requires a host with SHA-3 support (modern OpenSSL /
    /// Windows CNG). On a runtime without it (<see cref="Internals.OPaySignature.IsSha3Available"/>
    /// is false) verification cannot be performed and returns <c>false</c> after a warning — it never
    /// throws and never silently passes.</para>
    /// </summary>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        return RunWebhookVerify(() =>
        {
            if (string.IsNullOrWhiteSpace(_options.SecretKey))
            {
                Logger.LogWarning("OPay SecretKey not configured — callback signature verification cannot succeed.");
                return false;
            }

            if (!OPaySignature.IsSha3Available)
            {
                Logger.LogWarning(
                    "OPay callbacks are signed with HMAC-SHA3-512 but this runtime has no SHA-3 support; " +
                    "callback signature cannot be verified here.");
                return false;
            }

            OPayWebhookEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<OPayWebhookEvent>(payload);
            }
            catch (JsonException)
            {
                return false;
            }

            var data = evt?.Payload;
            if (data is null) return false;

            // The supplied signature wins; otherwise fall back to the sha512 carried in the body.
            var provided = string.IsNullOrWhiteSpace(signature) ? evt?.Sha512 : signature;
            if (string.IsNullOrWhiteSpace(provided)) return false;

            var signContent = OPaySignature.BuildCallbackSignContent(
                amount: data.Amount?.Total.ToString(CultureInfo.InvariantCulture) ?? "0",
                currency: data.Amount?.Currency ?? string.Empty,
                reference: data.Reference ?? string.Empty,
                refunded: data.Refunded,
                status: data.Status ?? string.Empty,
                timestamp: data.Timestamp ?? string.Empty,
                token: data.Token ?? string.Empty,
                transactionId: data.TransactionId ?? string.Empty);

            var expected = OPaySignature.HmacSha3_512Hex(signContent, _options.SecretKey);

            // Constant-time, case-insensitive hex comparison (normalise both sides to lowercase).
            return SignatureHelpers.ConstantTimeEquals(expected, provided.ToLowerInvariant());
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
        // OPay's real payment-notification callback has a single envelope type, "transaction-status"
        // (https://documentation.opaycheckout.com/payment-notifications-callbacks). The lifecycle
        // lives in payload.status ∈ {INITIAL, PENDING, SUCCESS, FAIL, CLOSE}
        // (https://documentation.opaycheckout.com/query-payment-status), and payload.refunded marks
        // a refund notification. We classify off (refunded, status) rather than off the envelope type.
        var data = webhookEvent.Payload;
        var rawReference = data?.Reference ?? data?.OrderNo;
        if (data is null || string.IsNullOrEmpty(rawReference)) return null;

        var amount = (data.Amount?.Total ?? 0L) / 100m;
        var currency = data.Amount?.Currency ?? "NGN";
        var statusRaw = data.Status?.ToUpperInvariant() ?? string.Empty;
        var isSuccess = statusRaw is "SUCCESS" or "SUCCESSFUL";
        var isFailure = statusRaw is "FAIL" or "FAILED" or "CLOSE" or "CLOSED";

        // Refund notification: payload.refunded == true.
        if (data.Refunded)
        {
            return isFailure
                ? new RefundFailedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.Type,
                    Category = WebhookEventCategory.RefundFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data.FailureCode ?? data.Status,
                    FailureMessage = data.FailureReason ?? data.DisplayedFailure
                }
                : new RefundSucceededEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Refunded,
                    EventType = webhookEvent.Type,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = data.TransactionId ?? rawReference,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false
                };
        }

        if (isSuccess)
            return new ChargeSucceededEvent
            {
                GatewayReference = rawReference,
                Status = PaymentStatus.Completed,
                EventType = webhookEvent.Type,
                Category = WebhookEventCategory.ChargeSucceeded,
                Amount = amount,
                Currency = currency,
                CustomerId = data.UserId,
                PaymentMethodToken = data.InstrumentType ?? data.PayMethod
            };

        if (isFailure)
            return new ChargeFailedEvent
            {
                GatewayReference = rawReference,
                Status = PaymentStatus.Failed,
                EventType = webhookEvent.Type,
                Category = WebhookEventCategory.ChargeFailed,
                Amount = amount,
                Currency = currency,
                FailureCode = data.FailureCode ?? data.Status,
                FailureMessage = data.FailureReason ?? data.DisplayedFailure
            };

        // INITIAL / PENDING (or anything unrecognised) — surface as a pending charge so consumers can
        // wait for a terminal callback rather than dropping the event.
        return new ChargePendingEvent
        {
            GatewayReference = rawReference,
            Status = PaymentStatus.Pending,
            EventType = webhookEvent.Type,
            Category = WebhookEventCategory.ChargePending,
            Amount = amount,
            Currency = currency
        };
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object body, AuthMode authMode, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        // Two documented auth modes (see OPaySignature):
        //   PublicKeyBearer — cashier/create: Authorization: Bearer {PublicKey}
        //     (https://documentation.opaycheckout.com/cashier-create)
        //   SignedBody — refund/status/…: Authorization: Bearer {HMAC-SHA512(sorted-body, SecretKey)}
        //     (https://documentation.opaycheckout.com/api-signature)
        var bearer = authMode == AuthMode.PublicKeyBearer
            ? _options.PublicKey
            : OPaySignature.HmacSha512Hex(OPaySignature.CanonicaliseForSigning(json), _options.SecretKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
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

    /// <summary>ISO currency for an OPay International country (NG→NGN, EG→EGP, PK→PKR), defaulting to NGN.</summary>
    private static string CurrencyForCountry(string? country) => country?.ToUpperInvariant() switch
    {
        "EG" => "EGP",
        "PK" => "PKR",
        _ => "NGN"
    };

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

    // Refund response shape per https://documentation.opaycheckout.com/payment-refund:
    // { reference, originalReference, orderNo, originalOrderNo, country, refundAmount, orderStatus }.
    private sealed class OPayRefundData
    {
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("originalReference")] public string? OriginalReference { get; set; }
        [JsonPropertyName("orderNo")] public string? OrderNo { get; set; }
        [JsonPropertyName("originalOrderNo")] public string? OriginalOrderNo { get; set; }
        [JsonPropertyName("refundAmount")] public OPayAmount? RefundAmount { get; set; }
        [JsonPropertyName("orderStatus")] public string? OrderStatus { get; set; }
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

    // Callback envelope per https://documentation.opaycheckout.com/payment-notifications-callbacks:
    // { "payload": { … }, "sha512": "<hmac-sha3-512 hex>", "type": "transaction-status" }.
    private sealed class OPayWebhookEvent
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("sha512")] public string? Sha512 { get; set; }
        [JsonPropertyName("payload")] public OPayWebhookPayload? Payload { get; set; }
    }

    // Callback payload fields per the notifications doc:
    // amount, channel, country, currency, displayedFailure, fee, feeCurrency, instrumentType,
    // reference, refunded, status, timestamp, token, transactionId, updated_at.
    // amount is a scalar (minor units) with a sibling currency field (this is the callback shape;
    // the cashier/status RESPONSE nests amount as {total,currency} instead).
    private sealed class OPayWebhookPayload
    {
        [JsonPropertyName("amount")] public long AmountMinor { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("refunded")] public bool Refunded { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("instrumentType")] public string? InstrumentType { get; set; }
        [JsonPropertyName("channel")] public string? Channel { get; set; }
        [JsonPropertyName("displayedFailure")] public string? DisplayedFailure { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }

        // Tolerant extras — present on some OPay notification variants but not the core spec.
        [JsonPropertyName("orderNo")] public string? OrderNo { get; set; }
        [JsonPropertyName("userId")] public string? UserId { get; set; }
        [JsonPropertyName("payMethod")] public string? PayMethod { get; set; }
        [JsonPropertyName("failureCode")] public string? FailureCode { get; set; }
        [JsonPropertyName("failureReason")] public string? FailureReason { get; set; }

        /// <summary>Convenience accessor exposing amount as the SDK's {Total,Currency} shape.</summary>
        [JsonIgnore]
        public OPayAmount Amount => new() { Total = AmountMinor, Currency = Currency };
    }
}
