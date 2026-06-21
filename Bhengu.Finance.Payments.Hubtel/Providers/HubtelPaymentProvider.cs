// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net.Http.Headers;
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
using Bhengu.Finance.Payments.Hubtel.Configuration;
using Bhengu.Finance.Payments.Hubtel.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Hubtel.Providers;

/// <summary>
/// Hubtel (Ghana) payment gateway provider. Wraps the Hubtel Online Checkout (hosted redirect),
/// Merchant-Account refund and send-money (payout) APIs.
/// <para>Auth is HTTP Basic with <c>base64(ClientId:ClientSecret)</c>
/// (source: https://businessdocs-developers.hubtel.com/docs/api-reference-online-checkout).</para>
/// <para>Hubtel splits its surface across two hosts, so each operation targets its documented host
/// via an absolute URI:</para>
/// <list type="bullet">
///   <item><description>Checkout initiate: <c>POST https://payproxyapi.hubtel.com/items/initiate</c></description></item>
///   <item><description>Refund: <c>POST https://api.hubtel.com/v1/merchantaccount/merchants/{posSalesNumber}/transactions/refund</c></description></item>
///   <item><description>Send money (payout): <c>POST https://api.hubtel.com/v1/merchantaccount/merchants/{posSalesNumber}/send/mobilemoney</c></description></item>
/// </list>
/// <para>Callbacks are NOT signed by Hubtel (no documented HMAC/signature header); see
/// <see cref="VerifyWebhookSignature"/>.</para>
/// </summary>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "Wire format built from public documentation; never sandbox-verified.")]
public sealed class HubtelPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly HubtelOptions _options;
    private readonly IBhenguDistributedCache? _idempotencyCache;
    private readonly Uri _checkoutBase;
    private readonly Uri _merchantBase;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Hubtel;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the Hubtel payment provider. <paramref name="idempotencyCache"/> is optional — when omitted, idempotency replay is a no-op.</summary>
    public HubtelPaymentProvider(
        HttpClient httpClient,
        IOptions<HubtelOptions> options,
        ILogger<HubtelPaymentProvider> logger,
        IBhenguDistributedCache? idempotencyCache = null)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotencyCache = idempotencyCache;

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(HubtelOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(HubtelOptions.ClientSecret)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantAccountNumber))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(HubtelOptions.MerchantAccountNumber)} is required");

        // Hubtel uses two hosts (see class docs). Every call below targets an absolute URI built from
        // these, so BaseAddress is only set as an inert default for the checkout host when unset.
        _checkoutBase = _options.ResolvedCheckoutBaseUrl;
        _merchantBase = _options.ResolvedMerchantBaseUrl;

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = _checkoutBase;

        // Basic auth: base64(ClientId:ClientSecret).
        // Source: https://businessdocs-developers.hubtel.com/docs/api-reference-online-checkout
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, () => ProcessPaymentCoreAsync(request, ct), ct);
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
        var cached = await TryGetCachedAsync<PaymentResponse>(request.IdempotencyKey, "charge", ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        // Online Checkout request body (camelCase). Fields per
        // https://businessdocs-developers.hubtel.com/docs/api-reference-online-checkout
        // (totalAmount, description, callbackUrl, returnUrl, merchantAccountNumber, cancellationUrl,
        //  clientReference; "title" is an optional display field, omitted here). The documented body has
        //  no payee fields, so the previously-sent payeeName/payeeMobileNumber/payeeEmail are dropped.
        var clientReference = request.PaymentMethodToken;
        var body = new
        {
            totalAmount = request.Amount,
            description = request.Description,
            callbackUrl = _options.CallbackUrl,
            returnUrl = _options.ReturnUrl,
            merchantAccountNumber = _options.MerchantAccountNumber,
            cancellationUrl = string.IsNullOrWhiteSpace(_options.CancellationUrl) ? _options.ReturnUrl : _options.CancellationUrl,
            clientReference
        };

        // POST https://payproxyapi.hubtel.com/items/initiate
        // Source: https://businessdocs-developers.hubtel.com/docs/api-reference-online-checkout
        var url = new Uri(_checkoutBase, "items/initiate").ToString();
        var responseBody = await HubtelHttpClient.SendAsync(_httpClient, Logger, HttpMethod.Post, url, body, ct, "ProcessPayment").ConfigureAwait(false);
        var checkout = JsonSerializer.Deserialize<HubtelCheckoutResponse>(responseBody, HubtelHttpClient.Json);

        Logger.LogInformation("Hubtel checkout initiated: id={Id} url={Url}",
            checkout?.Data?.CheckoutId, checkout?.Data?.CheckoutUrl);

        var response = new PaymentResponse
        {
            GatewayReference = checkout?.Data?.CheckoutId ?? clientReference,
            Status = PaymentStatus.Pending,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            // checkoutUrl is the user-facing hosted page; checkoutDirectUrl is a deep-link alternative.
            RedirectUrl = checkout?.Data?.CheckoutUrl ?? checkout?.Data?.CheckoutDirectUrl,
            Message = checkout?.Message
        };

        await TrySetCachedAsync(request.IdempotencyKey, "charge", response, ct).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunRefundAsync(request.GatewayReference, () => ProcessRefundCoreAsync(request, ct), ct);
    }

    private async Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
        var cached = await TryGetCachedAsync<RefundResponse>(request.IdempotencyKey, "refund", ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        // Refund body. Field names per the Merchant-Account refund endpoint
        // (TransactionId / Amount / Description / ClientReference).
        var body = new
        {
            transactionId = request.GatewayReference,
            amount = request.Amount,
            description = request.Reason,
            clientReference = request.IdempotencyKey ?? $"rf-{Guid.NewGuid():N}"
        };

        // POST https://api.hubtel.com/v1/merchantaccount/merchants/{posSalesNumber}/transactions/refund
        // Source: Merchant Account API (developers.hubtel.com); path corroborated by the official-shape
        // client paulmajora/hubtelpayment (/v1/merchantaccount/merchants/{account}/transactions/refund).
        var url = new Uri(_merchantBase, $"v1/merchantaccount/merchants/{Uri.EscapeDataString(_options.MerchantAccountNumber)}/transactions/refund").ToString();
        var responseBody = await HubtelHttpClient.SendAsync(_httpClient, Logger, HttpMethod.Post, url, body, ct, "ProcessRefund").ConfigureAwait(false);
        var refund = JsonSerializer.Deserialize<HubtelRefundResponse>(responseBody, HubtelHttpClient.Json);

        Logger.LogInformation("Hubtel refund: id={Id} status={Status}", refund?.Data?.TransactionId, refund?.Data?.Status);

        var response = new RefundResponse
        {
            GatewayReference = refund?.Data?.TransactionId ?? request.GatewayReference,
            Amount = request.Amount,
            Status = MapStatus(refund?.Data?.Status ?? "pending"),
            ProcessedAt = DateTime.UtcNow,
            Message = refund?.Message
        };

        await TrySetCachedAsync(request.IdempotencyKey, "refund", response, ct).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc/>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunPayoutAsync(request.Currency, () => ProcessPayoutCoreAsync(request, ct), ct);
    }

    private async Task<PayoutResponse> ProcessPayoutCoreAsync(PayoutRequest request, CancellationToken ct)
    {
        var cached = await TryGetCachedAsync<PayoutResponse>(request.IdempotencyKey, "payout", ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        // DestinationToken format: "<channel>:<msisdn>" e.g. "mtn-gh:233244000000"
        var colon = request.DestinationToken.IndexOf(':');
        if (colon <= 0)
            throw new BhenguPaymentException(ProviderName,
                "Hubtel PayoutRequest.DestinationToken must be '<channel>:<msisdn>' (channel one of mtn-gh|vodafone-gh|tigo-gh)");

        var channel = request.DestinationToken[..colon];
        var msisdn = request.DestinationToken[(colon + 1)..];
        var clientReference = request.IdempotencyKey ?? $"po-{Guid.NewGuid():N}";

        // Send Mobile Money body. Field names per the Merchant-Account Send-Money API
        // (RecipientName / RecipientMsisdn / CustomerEmail / Channel / Amount / PrimaryCallbackUrl /
        //  Description / ClientReference). Source: developers.hubtel.com (Merchant Account API).
        var body = new
        {
            RecipientName = request.Description,
            RecipientMsisdn = msisdn,
            Channel = channel,
            Amount = request.Amount,
            PrimaryCallbackUrl = _options.CallbackUrl,
            Description = request.Description,
            ClientReference = clientReference
        };

        // POST https://api.hubtel.com/v1/merchantaccount/merchants/{posSalesNumber}/send/mobilemoney
        // Source: Merchant Account API (developers.hubtel.com); base host + path corroborated by the
        // official-shape PHP/JS clients (ovac/hubtel-payment Api.php = https://api.hubtel.com/v1/merchantaccount/merchants/).
        var url = new Uri(_merchantBase, $"v1/merchantaccount/merchants/{Uri.EscapeDataString(_options.MerchantAccountNumber)}/send/mobilemoney").ToString();
        var responseBody = await HubtelHttpClient.SendAsync(_httpClient, Logger, HttpMethod.Post, url, body, ct, "ProcessPayout").ConfigureAwait(false);
        var payout = JsonSerializer.Deserialize<HubtelPayoutResponse>(responseBody, HubtelHttpClient.Json);

        Logger.LogInformation("Hubtel send-money payout: id={Id} status={Status} channel={Channel}",
            payout?.Data?.TransactionId, payout?.Data?.TransactionStatus, channel);

        var response = new PayoutResponse
        {
            GatewayReference = payout?.Data?.TransactionId ?? clientReference,
            Status = MapStatus(payout?.Data?.TransactionStatus ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };

        await TrySetCachedAsync(request.IdempotencyKey, "payout", response, ct).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Verify a Hubtel callback signature.
    /// <para>
    /// UNVERIFIED / non-standard: Hubtel's Online Checkout callback is NOT cryptographically signed —
    /// there is no documented signature header or HMAC on the callback POST
    /// (businessdocs-developers.hubtel.com/reference/checkout-callback shows none; the gap is noted in
    /// community write-ups, e.g. medium.com/@verbsgh ".../security-matters..."). Hubtel's intended
    /// authenticity model is to re-confirm the transaction via its status API and treat the
    /// <c>ClientReference</c> as single-use.
    /// </para>
    /// <para>
    /// This method therefore only succeeds when <see cref="HubtelOptions.WebhookSecret"/> is set, in
    /// which case it checks an HMAC-SHA256(hex) over the raw body — useful ONLY when the deployment
    /// fronts the callback with its own signing proxy. With no secret configured it returns
    /// <c>false</c>, signalling callers to fall back to status-API verification rather than trusting
    /// the payload blindly.
    /// </para>
    /// </summary>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            Logger.LogWarning(
                "Hubtel callbacks are not signed by Hubtel and no WebhookSecret proxy guard is configured — " +
                "treat the callback as unverified and confirm via the Hubtel status API before fulfilment.");
            return RunWebhookVerify(() => false);
        }

        return RunWebhookVerify(() => SignatureHelpers.VerifyHmacSha256(payload, signature, _options.WebhookSecret));
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return RunOperationAsync("parse_webhook", () => ParseWebhookCoreAsync(payload), ct);
    }

    private Task<WebhookEvent?> ParseWebhookCoreAsync(string payload)
    {
        try
        {
            var evt = JsonSerializer.Deserialize<HubtelWebhookEvent>(payload, HubtelHttpClient.Json);
            var data = evt?.Data;
            // Real callbacks carry no top-level "type"; identity comes from ClientReference, else any of
            // the Hubtel transaction ids (CheckoutId / SalesInvoiceId / TransactionId).
            var reference = data?.ClientReference ?? data?.TransactionId ?? data?.CheckoutId ?? data?.SalesInvoiceId;
            if (evt is null || data is null || string.IsNullOrEmpty(reference))
                return Task.FromResult<WebhookEvent?>(null);

            Logger.LogInformation("Parsed Hubtel webhook event: type={Type} responseCode={Code} status={Status}",
                evt.Type, evt.ResponseCode, data.Status);

            var typedEvent = MapToTypedEvent(evt, data, reference);
            return Task.FromResult(typedEvent);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse Hubtel webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private static WebhookEvent? MapToTypedEvent(HubtelWebhookEvent evt, HubtelWebhookData data, string reference)
    {
        var type = evt.Type;
        // Status precedence: Data.Status (real callback) → top-level Status. ResponseCode "0000" means a
        // successful Online Checkout payment even if a textual status is absent.
        // Source: https://businessdocs-developers.hubtel.com/reference/checkout-callback
        var rawStatus = data.Status ?? evt.Status;
        var statusLower = rawStatus?.ToLowerInvariant();
        if (statusLower is null && evt.ResponseCode == "0000") statusLower = "success";
        var typeLower = type?.ToLowerInvariant();
        var currency = data.Currency ?? "GHS";
        // Hubtel's transaction identifier for a charge is CheckoutId/SalesInvoiceId; TransactionId is used
        // by the Merchant-Account (refund/payout) callbacks.
        var txnId = data.TransactionId ?? data.CheckoutId ?? data.SalesInvoiceId ?? reference;

        return (typeLower, statusLower) switch
        {
            ("refund.completed", _) or (_, "refunded") => new RefundSucceededEvent
            {
                GatewayReference = reference,
                Status = PaymentStatus.Refunded,
                EventType = type ?? rawStatus,
                Category = WebhookEventCategory.RefundSucceeded,
                RefundReference = txnId,
                Amount = data.Amount,
                Currency = currency,
                IsPartial = false
            },
            ("payout.completed", _) => new PayoutCompletedEvent
            {
                GatewayReference = reference,
                Status = PaymentStatus.Completed,
                EventType = type ?? rawStatus,
                Category = WebhookEventCategory.PayoutCompleted,
                PayoutReference = txnId,
                Amount = data.Amount,
                Currency = currency
            },
            ("payout.failed", _) => new PayoutFailedEvent
            {
                GatewayReference = reference,
                Status = PaymentStatus.Failed,
                EventType = type ?? rawStatus,
                Category = WebhookEventCategory.PayoutFailed,
                PayoutReference = txnId,
                Amount = data.Amount,
                Currency = currency,
                FailureMessage = rawStatus
            },
            (_, "success") or (_, "paid") or (_, "completed") => new ChargeSucceededEvent
            {
                GatewayReference = reference,
                Status = PaymentStatus.Completed,
                EventType = type ?? rawStatus,
                Category = WebhookEventCategory.ChargeSucceeded,
                Amount = data.Amount,
                Currency = currency
            },
            (_, "failed") or (_, "declined") => new ChargeFailedEvent
            {
                GatewayReference = reference,
                Status = PaymentStatus.Failed,
                EventType = type ?? rawStatus,
                Category = WebhookEventCategory.ChargeFailed,
                Amount = data.Amount,
                Currency = currency,
                FailureMessage = rawStatus
            },
            (_, "cancelled") or (_, "canceled") => new WebhookEvent
            {
                GatewayReference = reference,
                Status = PaymentStatus.Cancelled,
                EventType = type ?? rawStatus,
                Category = WebhookEventCategory.Unknown
            },
            (_, "pending") or (_, "processing") => new ChargePendingEvent
            {
                GatewayReference = reference,
                Status = PaymentStatus.Pending,
                EventType = type ?? rawStatus,
                Category = WebhookEventCategory.ChargePending,
                Amount = data.Amount,
                Currency = currency
            },
            _ => null
        };
    }

    private async Task<T?> TryGetCachedAsync<T>(string? idempotencyKey, string operation, CancellationToken ct) where T : class
    {
        if (_idempotencyCache is null || string.IsNullOrWhiteSpace(idempotencyKey))
            return null;
        var key = $"hubtel:{operation}:{idempotencyKey}";
        return await _idempotencyCache.GetAsync<T>(key, ct).ConfigureAwait(false);
    }

    private async Task TrySetCachedAsync<T>(string? idempotencyKey, string operation, T value, CancellationToken ct) where T : class
    {
        if (_idempotencyCache is null || string.IsNullOrWhiteSpace(idempotencyKey))
            return;
        var key = $"hubtel:{operation}:{idempotencyKey}";
        await _idempotencyCache.SetAsync(key, value, TimeSpan.FromHours(24), ct).ConfigureAwait(false);
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "success" or "successful" or "paid" or "completed" => PaymentStatus.Completed,
        "pending" or "processing" => PaymentStatus.Pending,
        "failed" or "declined" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    private sealed class HubtelCheckoutResponse
    {
        [JsonPropertyName("responseCode")] public string? ResponseCode { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public HubtelCheckoutData? Data { get; set; }
    }

    // Online Checkout initiate response data. Fields per
    // https://businessdocs-developers.hubtel.com/docs/api-reference-online-checkout
    // (responseCode "0000" on success; data.checkoutUrl is the hosted page, data.checkoutDirectUrl a
    //  direct deep-link, data.checkoutId the Hubtel checkout identifier).
    private sealed class HubtelCheckoutData
    {
        [JsonPropertyName("checkoutUrl")] public string? CheckoutUrl { get; set; }
        [JsonPropertyName("checkoutDirectUrl")] public string? CheckoutDirectUrl { get; set; }
        [JsonPropertyName("checkoutId")] public string? CheckoutId { get; set; }
        [JsonPropertyName("clientReference")] public string? ClientReference { get; set; }
    }

    private sealed class HubtelRefundResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public HubtelRefundData? Data { get; set; }
    }

    private sealed class HubtelRefundData
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class HubtelPayoutResponse
    {
        [JsonPropertyName("responseCode")] public string? ResponseCode { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public HubtelPayoutData? Data { get; set; }
    }

    private sealed class HubtelPayoutData
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("clientReference")] public string? ClientReference { get; set; }
        [JsonPropertyName("transactionStatus")] public string? TransactionStatus { get; set; }
    }

    // Callback envelope. The real Hubtel Online Checkout callback is PascalCase with no "type" field —
    // { "ResponseCode":"0000", "Status":"Success", "Data":{ "CheckoutId", "SalesInvoiceId",
    //   "ClientReference", "Status", "Amount", "CustomerPhoneNumber", "PaymentDetails", "Description" } }.
    // Source: https://businessdocs-developers.hubtel.com/reference/checkout-callback (shape also quoted
    // verbatim in community integrations). Parsing is case-insensitive (HubtelHttpClient.Json), so these
    // camelCase property names bind the PascalCase callback. The optional "type" field supports the
    // SDK's typed event labels (payment.completed / refund.completed / payout.completed / payment.failed).
    private sealed class HubtelWebhookEvent
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("responseCode")] public string? ResponseCode { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("data")] public HubtelWebhookData? Data { get; set; }
    }

    private sealed class HubtelWebhookData
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("checkoutId")] public string? CheckoutId { get; set; }
        [JsonPropertyName("salesInvoiceId")] public string? SalesInvoiceId { get; set; }
        [JsonPropertyName("clientReference")] public string? ClientReference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("customerPhoneNumber")] public string? CustomerPhoneNumber { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }
}
