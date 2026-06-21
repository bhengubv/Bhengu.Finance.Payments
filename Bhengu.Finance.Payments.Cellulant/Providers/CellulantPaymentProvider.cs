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
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Cellulant.Providers;

/// <summary>
/// Cellulant (Tingg) pan-African aggregator. Wraps the Tingg Checkout 3.0 Express Checkout API for
/// collections (<c>/v3/checkout-api/checkout-request/express-request</c>) and the Checkout refund
/// endpoint (<c>/v3/checkout-api/refund/request</c>); payouts use the SEPARATE Tingg Payouts
/// "global-api" product (<c>/v1/global-api/payments</c>). OAuth2 access tokens are minted on demand
/// using the configured client credentials, and EVERY call additionally carries the Tingg
/// <c>apiKey</c> header. Honours per-call <c>IdempotencyKey</c> by dedup'ing via the shared
/// <see cref="IBhenguDistributedCache"/> for 24 hours.
/// </summary>
/// <remarks>
/// Wire details verified against Tingg docs (June 2026):
/// hosts https://docs.tingg.africa/reference/authenticate-requests ;
/// express checkout https://docs.tingg.africa/docs/checkout-v3-express-checkout ;
/// refund https://docs.tingg.africa/reference/refund ;
/// query https://docs.tingg.africa/reference/query-status ;
/// callback/webhook https://docs.tingg.africa/reference/4-implement-webhook-via-callback-url-1 ;
/// payouts https://docs.tingg.africa/reference/postpayment .
/// </remarks>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "Wire format built from public Tingg Checkout 3.0 docs (docs.tingg.africa, June 2026); never sandbox-verified. Webhook signature scheme + payout body are UNVERIFIED — see code.")]
public sealed class CellulantPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly CellulantOptions _options;
    private readonly IBhenguDistributedCache _cache;
    private readonly CellulantTokenBroker _tokenBroker;
    private static readonly TimeSpan s_idempotencyTtl = TimeSpan.FromHours(24);

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Cellulant;

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
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
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
            // Verified Tingg Checkout 3.0 hosts. Source: https://docs.tingg.africa/reference/authenticate-requests
            var defaultUrl = _options.UseSandbox
                ? "https://api-approval.tingg.africa/"
                : "https://api.tingg.africa/";
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? defaultUrl);
        }
    }

    private static string PayoutBaseUrl(CellulantOptions options) =>
        options.PayoutBaseUrl
        ?? (options.UseSandbox ? "https://api-approval.tingg.africa/" : "https://api.tingg.africa/");

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, async () =>
        {
            var cached = await TryGetCachedAsync<PaymentResponse>(request.IdempotencyKey, "charge", ct).ConfigureAwait(false);
            if (cached is not null) return cached;

            var email = request.Metadata?.GetValueOrDefault("email");
            var firstName = request.Metadata?.GetValueOrDefault("firstName")
                ?? request.Metadata?.GetValueOrDefault("name") ?? "Customer";
            var lastName = request.Metadata?.GetValueOrDefault("lastName") ?? "Customer";
            var msisdn = request.PaymentMethodToken;

            var merchantTransactionId = request.IdempotencyKey
                ?? (string.IsNullOrEmpty(_options.MerchantTransactionId)
                    ? $"tingg-{Guid.NewGuid():N}"
                    : $"{_options.MerchantTransactionId}-{Guid.NewGuid():N}");

            // Verified body (snake_case). Source: https://docs.tingg.africa/docs/checkout-v3-express-checkout
            // amount/codes are strings on the wire in Tingg's example; account_number must be <=15 chars
            // with no special chars, so we derive a safe reference from the merchant transaction id.
            var accountNumber = BuildAccountNumber(merchantTransactionId);
            var requestBody = new
            {
                customer_first_name = firstName,
                customer_last_name = lastName,
                customer_email = email,
                msisdn,
                account_number = accountNumber,
                request_amount = request.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                merchant_transaction_id = merchantTransactionId,
                service_code = _options.ServiceCode,
                country_code = _options.CountryCode,
                currency_code = request.Currency.ToUpperInvariant(),
                request_description = request.Description,
                due_date = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                language_code = "en",
                callback_url = _options.CallbackUrl,
                success_redirect_url = _options.CallbackUrl,
                fail_redirect_url = _options.CallbackUrl
            };

            var body = await SendAuthorisedAsync(HttpMethod.Post, "v3/checkout-api/checkout-request/express-request", requestBody, ct, "ProcessPayment").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<CellulantCheckoutResponse>(body);

            // Express response carries no checkout id — the merchant_transaction_id is the correlation
            // key; the customer-facing payment page is results.short_url.
            // Source: https://docs.tingg.africa/docs/checkout-v3-express-checkout
            var statusCode = response?.Status?.StatusCode;
            Logger.LogInformation("Cellulant checkout created: txn={Txn} status={Status}",
                merchantTransactionId, statusCode);

            var pr = new PaymentResponse
            {
                GatewayReference = merchantTransactionId,
                // A successful express-request returns the hosted page; the actual payment is still
                // pending until the customer completes it on the page (confirmed via webhook/query).
                Status = statusCode == 200 ? PaymentStatus.Pending : PaymentStatus.Failed,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                RedirectUrl = response?.Results?.ShortUrl ?? response?.Results?.LongUrl,
                Message = response?.Status?.StatusDescription
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

            var payerTransactionId = request.IdempotencyKey ?? $"tingg-payout-{Guid.NewGuid():N}";

            // Verified host/path: POST {payout-host}/v1/global-api/payments for B2C mobile-money
            // disbursement. Source: https://docs.tingg.africa/reference/postpayment
            //
            // UNVERIFIED: the exact request/response JSON for global-api/payments is NOT fully
            // documented publicly. The docs show a function-envelope shape
            //   { function:"BEEP.postPayment", countryCode, payload:{ credentials:{username,password},
            //     packet:[{ serviceCode, MSISDN, accountNumber, payerTransactionID, amount, countryCode,
            //     currencyCode, datePaymentReceived, narration, customerNames, paymentMode,
            //     extraData:{ callbackUrl } }] } }
            // authenticated by username/password INSIDE the payload (NOT the checkout Bearer/apiKey).
            // We construct that documented envelope on a best-effort basis; the credential fields are
            // intentionally left unset because Tingg Payouts uses different credentials than Checkout
            // and the SDK has no verified field for them. DO NOT rely on payouts in production until
            // sandbox-verified. See README / ProviderVerificationStatus note.
            var requestBody = new
            {
                function = "BEEP.postPayment",
                countryCode = _options.CountryCode,
                payload = new
                {
                    packet = new[]
                    {
                        new
                        {
                            serviceCode = _options.ServiceCode,
                            MSISDN = request.DestinationToken,
                            accountNumber = request.DestinationToken,
                            payerTransactionID = payerTransactionId,
                            amount = request.Amount,
                            countryCode = _options.CountryCode,
                            currencyCode = request.Currency.ToUpperInvariant(),
                            narration = request.Description,
                            extraData = new { callbackUrl = _options.CallbackUrl }
                        }
                    }
                }
            };

            var body = await SendAuthorisedToAsync(PayoutBaseUrl(_options), HttpMethod.Post, "v1/global-api/payments", requestBody, ct, "ProcessPayout").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<CellulantPayoutResponse>(body);

            // UNVERIFIED: response field names below (results[].beepTransactionID/statusCode) follow the
            // partial docs at https://docs.tingg.africa/reference/postpayment but were not verified live.
            var result = response?.Results is { Length: > 0 } r ? r[0] : null;
            Logger.LogInformation("Cellulant Tingg payout posted: {Reference} status={Status}",
                result?.BeepTransactionId, result?.StatusCode);

            var pr = new PayoutResponse
            {
                GatewayReference = result?.BeepTransactionId ?? payerTransactionId,
                // 139 = "Payment posted successfully and pending acknowledgement" per the docs.
                Status = result?.StatusCode == 139 ? PaymentStatus.Pending : MapPayoutStatus(result?.StatusCode),
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
        ArgumentNullException.ThrowIfNull(request);
        return RunRefundAsync(request.GatewayReference, async () =>
        {
            var cached = await TryGetCachedAsync<RefundResponse>(request.IdempotencyKey, "refund", ct).ConfigureAwait(false);
            if (cached is not null) return cached;

            // Verified body. Source: https://docs.tingg.africa/reference/refund
            // GatewayReference holds our merchant_transaction_id (the correlation key from checkout).
            var refundReference = request.IdempotencyKey ?? $"tingg-refund-{Guid.NewGuid():N}";
            var requestBody = new
            {
                merchant_transaction_id = request.GatewayReference,
                // Verified values: "Full" | "Partial". amount is compulsory for partial refunds.
                // currency_code is optional (Tingg defaults to the original) and RefundRequest carries
                // no currency, so it is omitted.
                refund_type = request.IsPartial ? "Partial" : "Full",
                amount = request.Amount,
                refund_narration = string.IsNullOrWhiteSpace(request.Reason) ? "Refund" : request.Reason,
                refund_reference = refundReference,
                service_code = _options.ServiceCode
            };

            var body = await SendAuthorisedAsync(HttpMethod.Post, "v3/checkout-api/refund/request", requestBody, ct, "ProcessRefund").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<CellulantRefundResponse>(body);

            // Verified response is a status envelope (status.status_code 200 = "request logged").
            var statusCode = response?.Status?.StatusCode;
            Logger.LogInformation("Cellulant refund logged: {Reference} for txn {TransactionId} status={Status}",
                refundReference, request.GatewayReference, statusCode);

            var pr = new RefundResponse
            {
                GatewayReference = refundReference,
                Amount = request.Amount,
                // 200 = refund request successfully logged (async; final state arrives via webhook/query).
                Status = statusCode == 200 ? PaymentStatus.Pending : PaymentStatus.Failed,
                ProcessedAt = DateTime.UtcNow,
                Message = response?.Status?.StatusDescription
            };

            await TrySetCachedAsync(request.IdempotencyKey, "refund", pr, ct).ConfigureAwait(false);
            return pr;
        }, ct);
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
                Logger.LogWarning("Cellulant WebhookSecret not configured — signature verification cannot succeed.");
                return false;
            }
            // UNVERIFIED: Tingg's public Checkout v3 callback docs
            // (https://docs.tingg.africa/reference/4-implement-webhook-via-callback-url-1) do NOT
            // document any signature/HMAC header on the IPN — authenticity is not described there.
            // This retains the prior HMAC-SHA256 lowercase-hex check for deployments with an
            // out-of-band signing arrangement; it is NOT confirmed against Tingg. If Tingg does not
            // sign your callbacks, do NOT rely on this — authenticate by re-querying transaction
            // status via GET /v3/checkout-api/query/{service_code}/{merchant_transaction_id}.
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
                var webhookEvent = JsonSerializer.Deserialize<CellulantWebhookEvent>(payload);
                if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

                Logger.LogInformation("Parsed Cellulant webhook: txn={Txn} status={Status}",
                    webhookEvent.MerchantTransactionId, webhookEvent.RequestStatusCode);
                var typed = MapWebhookEvent(webhookEvent);
                return Task.FromResult(typed);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to parse Cellulant webhook event");
                return Task.FromResult<WebhookEvent?>(null);
            }
        }, ct);
    }

    // Verified Tingg Checkout v3 IPN/callback payload (snake_case, status-code driven). The
    // correlation key is merchant_transaction_id; checkout_request_id is Tingg's internal id.
    // Status codes: 178 = full payment, 179 = partial payment, 180 = payment rejected,
    // 184-187/191 = refund states. Sources:
    // https://docs.tingg.africa/reference/4-implement-webhook-via-callback-url-1 and
    // https://docs.tingg.africa/reference/query-status
    private static WebhookEvent? MapWebhookEvent(CellulantWebhookEvent webhookEvent)
    {
        var reference = webhookEvent.MerchantTransactionId ?? webhookEvent.CheckoutRequestId;
        if (string.IsNullOrEmpty(reference)) return null;

        var amount = webhookEvent.AmountPaid ?? webhookEvent.RequestAmount ?? 0m;
        var currency = webhookEvent.CurrencyCode ?? "KES";
        var statusCode = webhookEvent.RequestStatusCode;
        var statusDesc = webhookEvent.RequestStatusDescription;

        switch (statusCode)
        {
            // Successful collection (full or partial payment received).
            case 178:
            case 179:
                return new ChargeSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = statusDesc ?? statusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency,
                    CustomerId = webhookEvent.Msisdn,
                    PaymentMethodToken = webhookEvent.Msisdn
                };

            // Payment rejected / failed.
            case 180:
                return new ChargeFailedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Failed,
                    EventType = statusDesc ?? statusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = statusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    FailureMessage = statusDesc
                };

            // Refund completed (186 = refunded, 187 = refund acknowledged, 191 = refund settled).
            case 186:
            case 187:
            case 191:
                return new RefundSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Refunded,
                    EventType = statusDesc ?? statusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = webhookEvent.CheckoutRequestId ?? reference,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false
                };

            default:
                return null;
        }
    }

    /// <summary>Mint or return a cached Tingg OAuth2 access token. Internal; exposed for sibling providers.</summary>
    internal Task<string> EnsureAccessTokenAsync(CancellationToken ct) =>
        _tokenBroker.EnsureAccessTokenAsync(_httpClient, ct);

    private Task<string> SendAuthorisedAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation) =>
        SendAuthorisedToAsync(baseUrlOverride: null, method, path, body, ct, operation);

    /// <summary>
    /// Send an authorised request. Every Tingg call carries the OAuth Bearer token AND the
    /// <c>apiKey</c> header. Source: https://docs.tingg.africa/reference/authenticate-requests.
    /// When <paramref name="baseUrlOverride"/> is set the request targets an absolute URI on that
    /// host (used for the separate Payouts global-api host) rather than the client's BaseAddress.
    /// </summary>
    private async Task<string> SendAuthorisedToAsync(string? baseUrlOverride, HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var token = await _tokenBroker.EnsureAccessTokenAsync(_httpClient, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var requestUri = baseUrlOverride is null ? new Uri(path, UriKind.Relative) : new Uri(new Uri(baseUrlOverride), path);
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, requestUri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Tingg requires the apiKey header on every call. The checkout endpoints document it as
        // `apiKey`; the token/refund/query reference pages show `apikey`. HTTP header names are
        // case-insensitive (RFC 7230 §3.2), so the casing is immaterial on the wire.
        // Source: https://docs.tingg.africa/docs/checkout-v3-express-checkout
        if (!string.IsNullOrEmpty(_options.ApiKey))
            req.Headers.TryAddWithoutValidation("apiKey", _options.ApiKey);

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Cellulant {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    /// <summary>
    /// Tingg's <c>account_number</c> must be ≤15 chars with no special chars (underscores allowed).
    /// Derive a safe value from the merchant transaction id.
    /// Source: https://docs.tingg.africa/docs/checkout-v3-express-checkout
    /// </summary>
    private static string BuildAccountNumber(string merchantTransactionId)
    {
        var cleaned = new string(merchantTransactionId.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (cleaned.Length == 0) cleaned = Guid.NewGuid().ToString("N");
        return cleaned.Length <= 15 ? cleaned : cleaned[..15];
    }

    private static PaymentStatus MapPayoutStatus(int? statusCode) => statusCode switch
    {
        139 => PaymentStatus.Pending, // posted, pending acknowledgement
        null => PaymentStatus.Pending,
        _ => PaymentStatus.Pending
    };

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

    // === Cellulant API response shapes (internal) ===
    // Verified status envelope shared by express-request and refund/request:
    //   { "status": { "status_code": 200, "status_description": "success" },
    //     "results": { "short_url": "...", "long_url": "..." } }
    // Source: https://docs.tingg.africa/docs/checkout-v3-express-checkout

    private sealed class CellulantStatusEnvelope
    {
        [JsonPropertyName("status_code")] public int? StatusCode { get; set; }
        [JsonPropertyName("status_description")] public string? StatusDescription { get; set; }
    }

    private sealed class CellulantCheckoutResults
    {
        [JsonPropertyName("short_url")] public string? ShortUrl { get; set; }
        [JsonPropertyName("long_url")] public string? LongUrl { get; set; }
    }

    private sealed class CellulantCheckoutResponse
    {
        [JsonPropertyName("status")] public CellulantStatusEnvelope? Status { get; set; }
        [JsonPropertyName("results")] public CellulantCheckoutResults? Results { get; set; }
    }

    private sealed class CellulantRefundResponse
    {
        [JsonPropertyName("status")] public CellulantStatusEnvelope? Status { get; set; }
    }

    // UNVERIFIED: Tingg Payouts (global-api) response shape is only partially documented.
    // Source: https://docs.tingg.africa/reference/postpayment
    private sealed class CellulantPayoutResponse
    {
        [JsonPropertyName("authStatusCode")] public int? AuthStatusCode { get; set; }
        [JsonPropertyName("authStatusDescription")] public string? AuthStatusDescription { get; set; }
        [JsonPropertyName("results")] public CellulantPayoutResult[]? Results { get; set; }
    }

    private sealed class CellulantPayoutResult
    {
        [JsonPropertyName("statusCode")] public int? StatusCode { get; set; }
        [JsonPropertyName("statusDescription")] public string? StatusDescription { get; set; }
        [JsonPropertyName("payerTransactionID")] public string? PayerTransactionId { get; set; }
        [JsonPropertyName("beepTransactionID")] public string? BeepTransactionId { get; set; }
    }

    // Verified IPN/callback fields (subset we consume). snake_case.
    // Source: https://docs.tingg.africa/reference/4-implement-webhook-via-callback-url-1
    private sealed class CellulantWebhookEvent
    {
        [JsonPropertyName("checkout_request_id")] public string? CheckoutRequestId { get; set; }
        [JsonPropertyName("merchant_transaction_id")] public string? MerchantTransactionId { get; set; }
        [JsonPropertyName("request_status_code")] public int? RequestStatusCode { get; set; }
        [JsonPropertyName("request_status_description")] public string? RequestStatusDescription { get; set; }
        [JsonPropertyName("request_amount")] public decimal? RequestAmount { get; set; }
        [JsonPropertyName("amount_paid")] public decimal? AmountPaid { get; set; }
        [JsonPropertyName("currency_code")] public string? CurrencyCode { get; set; }
        [JsonPropertyName("account_number")] public string? AccountNumber { get; set; }
        [JsonPropertyName("service_code")] public string? ServiceCode { get; set; }
        [JsonPropertyName("MSISDN")] public string? Msisdn { get; set; }
    }
}
