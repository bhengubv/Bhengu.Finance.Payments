// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Ozow.Configuration;
using Bhengu.Finance.Payments.Ozow.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Ozow.Providers;

/// <summary>
/// Ozow payment gateway provider — instant EFT / pay-by-bank for South Africa.
///
/// <para><b>Charge is a REDIRECT flow, not a JSON API call.</b> Ozow's customer payment works by
/// building a signed request (the post-variables below plus a <c>HashCheck</c>) and sending the payer
/// to <c>https://pay.ozow.com/</c> to complete the payment in their bank. This provider therefore
/// returns a <see cref="PaymentResponse"/> with <see cref="PaymentStatus.Pending"/> and the
/// <c>pay.ozow.com</c> URL in <see cref="PaymentResponse.RedirectUrl"/> — it does NOT post the charge
/// to <c>api.ozow.com</c>. The payment outcome arrives asynchronously on the NotifyUrl webhook
/// (see <see cref="ParseWebhookAsync"/>) and can be reconciled via <see cref="GetTransactionByReferenceAsync"/>.</para>
///
/// <para>Sources for the wire format:
/// <list type="bullet">
/// <item>https://ozow.com/integrations — "you'll need to post the following variables to https://pay.ozow.com";
/// "a normal HTML form ... posted to Ozow, where customers are redirected ... to Ozow's payment page".</item>
/// <item>https://hub.ozow.com/docs — post-variables table and the HashCheck rule ("Concatenate the post
/// variables (excluding HashCheck) in the order they appear in the post variables table, append your
/// [private] key, convert to lowercase, generate a SHA512 hash").</item>
/// <item>Reference implementations agreeing on the field order + pay.ozow.com host:
/// react-native-ozow (npm) <c>src/utils/hashers.ts</c>; Laravel sample (medium.com/@respectmurimi2000);
/// wdtheprovider/ozpay (PHP); timm-oh gist.</item>
/// </list></para>
///
/// <para>Ozow's standard merchant API does NOT expose payouts — <see cref="IPayoutProvider"/> is
/// intentionally not implemented; merchants requiring disbursements use Ozow's separate Disbursement API.</para>
/// </summary>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "Redirect wire format + HashCheck built from public documentation (ozow.com/integrations, hub.ozow.com/docs); never sandbox-verified.")]
public sealed class OzowPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider
{
    /// <summary>Default customer-redirect host. Source: https://ozow.com/integrations.</summary>
    private const string DefaultPaymentBaseUrl = "https://pay.ozow.com/";

    /// <summary>Default server-side API host (transaction status). Source: https://hub.ozow.com/docs.</summary>
    private const string DefaultApiBaseUrl = "https://api.ozow.com/";

    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly OzowOptions _options;
    private readonly OzowIdempotencyCache _idempotency;
    private readonly Uri _paymentBaseUri;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Ozow;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.BankTransfer |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public OzowPaymentProvider(
        HttpClient httpClient,
        IOptions<OzowOptions> options,
        ILogger<OzowPaymentProvider> logger,
        OzowIdempotencyCache idempotency)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.SiteCode))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OzowOptions.SiteCode)} is required");
        if (string.IsNullOrWhiteSpace(_options.PrivateKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OzowOptions.PrivateKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OzowOptions.ApiKey)} is required");

        var paymentBase = string.IsNullOrWhiteSpace(_options.PaymentBaseUrl) ? DefaultPaymentBaseUrl : _options.PaymentBaseUrl!;
        if (!paymentBase.EndsWith('/')) paymentBase += "/";
        _paymentBaseUri = new Uri(paymentBase);

        // The injected HttpClient is only used for server-side api.ozow.com calls (transaction status).
        // The charge is a redirect and issues no HTTP request.
        if (_httpClient.BaseAddress is null)
        {
            var apiBase = string.IsNullOrWhiteSpace(_options.ApiBaseUrl) ? DefaultApiBaseUrl : _options.ApiBaseUrl!;
            if (!apiBase.EndsWith('/')) apiBase += "/";
            _httpClient.BaseAddress = new Uri(apiBase);
        }

        // Ozow authenticates server-side API calls with the API key on an "ApiKey" header.
        // Source: https://hub.ozow.com/docs ("Each API call needs an http header value with your API Key").
        if (!_httpClient.DefaultRequestHeaders.Contains("ApiKey"))
            _httpClient.DefaultRequestHeaders.Add("ApiKey", _options.ApiKey);
        if (!_httpClient.DefaultRequestHeaders.Accept.Any(h => h.MediaType == "application/json"))
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessPaymentCoreAsync(request, ct), ct);
    }

    /// <summary>
    /// Build the signed Ozow redirect. No HTTP call is made — the returned <see cref="PaymentResponse"/>
    /// carries the <c>pay.ozow.com</c> URL the payer must be sent to, and <see cref="PaymentStatus.Pending"/>.
    /// </summary>
    private Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
        => RunChargeAsync(request.Currency, () =>
        {
            var transactionReference = request.Metadata?.GetValueOrDefault("transaction_reference")
                ?? request.PaymentMethodToken;
            if (string.IsNullOrWhiteSpace(transactionReference))
                transactionReference = Guid.NewGuid().ToString("N");

            // Amount: decimal(9,2), formatted to two places with an invariant '.' separator.
            // Source: hub.ozow.com/docs (Amount, Decimal(9,2)); Laravel sample number_format($amount, 2, '.', '').
            var amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture);
            var currencyCode = request.Currency.ToUpperInvariant();
            var countryCode = _options.CountryCode.ToUpperInvariant();
            // BankReference: String(20). Ozow rejects longer values, so clamp the human description.
            // Source: hub.ozow.com/docs (BankReference, String(20), required).
            var bankReference = Truncate(request.Description, 20);
            var cancelUrl = request.Metadata?.GetValueOrDefault("cancel_url") ?? string.Empty;
            var errorUrl = request.Metadata?.GetValueOrDefault("error_url") ?? string.Empty;
            var successUrl = request.Metadata?.GetValueOrDefault("success_url") ?? string.Empty;
            var notifyUrl = request.Metadata?.GetValueOrDefault("notify_url") ?? string.Empty;
            // IsTest is serialised as the lowercase string "true"/"false" — it is part of the hash exactly
            // as it appears on the wire. Source: react-native-ozow hashers.ts; hub.ozow.com/docs (IsTest, boolean).
            var isTest = _options.UseSandbox ? "true" : "false";

            // === HashCheck ===
            // Concatenate the post variables IN POST-VARIABLES-TABLE ORDER (excluding HashCheck), append the
            // PrivateKey, lowercase the whole string, then SHA-512 (hex). This core ordered set is the one all
            // independent references agree on byte-for-byte (react-native-ozow legacy hasher, wdtheprovider PHP,
            // Laravel sample): SiteCode, CountryCode, CurrencyCode, Amount, TransactionReference, BankReference,
            // CancelUrl, ErrorUrl, SuccessUrl, NotifyUrl, IsTest, + PrivateKey.
            // Source: https://hub.ozow.com/docs (HashCheck rule); https://ozow.com/integrations.
            // NOTE: Ozow also defines optional Customer / Optional1-5 fields that, when used, slot into the
            // table BETWEEN BankReference and CancelUrl — but public references disagree on the exact
            // Customer-vs-Optional ordering, so this provider does not emit them rather than risk a
            // hash mismatch on a live charge. See the UNVERIFIED note below.
            var orderedHashFields = new[]
            {
                _options.SiteCode,
                countryCode,
                currencyCode,
                amount,
                transactionReference,
                bankReference,
                cancelUrl,
                errorUrl,
                successUrl,
                notifyUrl,
                isTest,
            };
            // Ozow's rule: "convert the concatenated string to lowercase, then generate a SHA512 hash".
            // The lowercasing happens HERE (on the full concatenation, PrivateKey included), exactly as the
            // PHP/Laravel/react-native references do (strtolower(implode(...) . $privateKey)).
            var hashInput = string.Concat(string.Concat(orderedHashFields), _options.PrivateKey).ToLowerInvariant();
            var hashCheck = GenerateSha512HashHex(hashInput);

            // === Build the redirect ===
            // Ozow accepts the request both as an auto-submitting HTML POST form AND as a GET query string to
            // pay.ozow.com (the Laravel reference uses pay.ozow.com?<querystring> then redirects). We emit the
            // GET query-string URL so callers can 302 the payer straight there with no intermediate form — the
            // same approach the iPay redirect provider in this repo uses.
            // Every field that fed the HashCheck is sent on the wire with the IDENTICAL value (empty strings
            // included) so the values Ozow re-hashes match exactly; HashCheck is appended last. Wire field
            // names are the post-variables-table names (PascalCase).
            var pairs = new (string Key, string Value)[]
            {
                ("SiteCode", _options.SiteCode),
                ("CountryCode", countryCode),
                ("CurrencyCode", currencyCode),
                ("Amount", amount),
                ("TransactionReference", transactionReference),
                ("BankReference", bankReference),
                ("CancelUrl", cancelUrl),
                ("ErrorUrl", errorUrl),
                ("SuccessUrl", successUrl),
                ("NotifyUrl", notifyUrl),
                ("IsTest", isTest),
                ("HashCheck", hashCheck),
            };
            var query = string.Join('&', pairs
                .Select(p => $"{WebUtility.UrlEncode(p.Key)}={WebUtility.UrlEncode(p.Value)}"));
            var redirectUrl = new Uri(_paymentBaseUri, "?" + query).ToString();

            if (!string.IsNullOrEmpty(request.PaymentMethodToken))
                Logger.LogDebug("Ozow ProcessPayment built redirect for reference={Reference}", transactionReference);
            Logger.LogInformation("Ozow redirect built for reference={Reference} amount={Amount} {Currency}",
                transactionReference, amount, currencyCode);

            return Task.FromResult(new PaymentResponse
            {
                // No Ozow transaction id exists yet (it's assigned when the payer lands on pay.ozow.com);
                // the merchant's own TransactionReference is the correlation key until the webhook arrives.
                GatewayReference = transactionReference,
                Status = PaymentStatus.Pending,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                RedirectUrl = redirectUrl,
                Message = "Redirect the payer to RedirectUrl to complete the Ozow payment."
            });
        }, ct);

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessRefundCoreAsync(request, ct), ct);
    }

    private Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
        => RunRefundAsync(request.GatewayReference, async () =>
        {
            // UNVERIFIED: Ozow does offer refunds, but a public, authoritative request/response spec for the
            // refund endpoint (host + path + body + hash) was not found in the developer docs at the time of
            // writing — refunds are documented as initiated from the merchant portal / a separate guide. The
            // request below preserves the prior SDK behaviour (POST {api.ozow.com}/refund with siteCode +
            // transactionId + amount + reason) so refund wiring stays callable, but the exact wire contract is
            // NOT confirmed against Ozow's docs and MUST be validated before production use.
            var requestBody = new
            {
                siteCode = _options.SiteCode,
                transactionId = request.GatewayReference,
                amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
                reason = request.Reason
            };

            var body = await SendApiAsync(HttpMethod.Post, "refund", requestBody, ct, "ProcessRefund").ConfigureAwait(false);
            var refundResponse = JsonSerializer.Deserialize<OzowRefundResponse>(body, DeserializeOptions);

            Logger.LogInformation("Ozow refund created: {RefundId} for transaction {TransactionId}",
                refundResponse?.RefundId, request.GatewayReference);

            var status = MapStatus(refundResponse?.Status ?? "pending");

            return new RefundResponse
            {
                GatewayReference = refundResponse?.RefundId ?? string.Empty,
                Amount = request.Amount,
                Status = status,
                ProcessedAt = DateTime.UtcNow,
                Message = refundResponse?.Status
            };
        }, ct);

    /// <summary>
    /// Query a transaction by the merchant's own reference via Ozow's server-side API.
    /// <c>GET https://api.ozow.com/GetTransactionByReference?siteCode={siteCode}&amp;transactionReference={ref}</c>,
    /// authenticated with the <c>ApiKey</c> header. Ozow may return up to 10 results (duplicate references are
    /// allowed), so the caller gets the raw JSON array as returned. Returns the raw response body.
    /// Source: https://hub.ozow.com/docs ("GetTransactionByReference").
    /// </summary>
    public Task<string> GetTransactionByReferenceAsync(string transactionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(transactionReference);
        return RunOperationAsync("get_transaction_by_reference", () =>
        {
            var path = $"GetTransactionByReference?siteCode={Uri.EscapeDataString(_options.SiteCode)}" +
                       $"&transactionReference={Uri.EscapeDataString(transactionReference)}";
            return SendApiAsync(HttpMethod.Get, path, body: null, ct, "GetTransactionByReference");
        }, ct);
    }

    /// <summary>
    /// Query a transaction by Ozow's own transaction id via Ozow's server-side API.
    /// <c>GET https://api.ozow.com/GetTransaction?siteCode={siteCode}&amp;transactionId={id}</c>,
    /// authenticated with the <c>ApiKey</c> header. Returns the raw response body.
    /// Source: https://hub.ozow.com/docs ("GetTransaction").
    /// </summary>
    public Task<string> GetTransactionAsync(string transactionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(transactionId);
        return RunOperationAsync("get_transaction", () =>
        {
            var path = $"GetTransaction?siteCode={Uri.EscapeDataString(_options.SiteCode)}" +
                       $"&transactionId={Uri.EscapeDataString(transactionId)}";
            return SendApiAsync(HttpMethod.Get, path, body: null, ct, "GetTransaction");
        }, ct);
    }

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        return RunWebhookVerify(() =>
        {
            if (string.IsNullOrWhiteSpace(_options.PrivateKey))
            {
                Logger.LogWarning("Ozow PrivateKey not configured — signature verification cannot succeed.");
                return false;
            }

            try
            {
                // Ozow's notification Hash uses the same construction as the request: concatenate the
                // notification variables (excluding Hash) in table order, append the PrivateKey, lowercase,
                // SHA-512 (hex). The caller passes the already-concatenated notification string as `payload`.
                // Source: https://hub.ozow.com/docs (notification Hash rule).
                var hashInput = payload + _options.PrivateKey;
                var computedHash = GenerateSha512HashHex(hashInput);

                return CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                    Encoding.UTF8.GetBytes(computedHash));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Ozow webhook signature verification raised");
                return false;
            }
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
                var webhookEvent = JsonSerializer.Deserialize<OzowWebhookNotification>(payload, DeserializeOptions);
                if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

                Logger.LogInformation("Parsed Ozow webhook event: TransactionId={TransactionId} status={Status}",
                    webhookEvent.TransactionId, webhookEvent.Status);

                var reference = !string.IsNullOrEmpty(webhookEvent.TransactionReference)
                    ? webhookEvent.TransactionReference
                    : webhookEvent.TransactionId;
                if (string.IsNullOrEmpty(reference))
                    return Task.FromResult<WebhookEvent?>(null);

                var status = MapStatus(webhookEvent.Status ?? "");
                var amount = webhookEvent.Amount ?? 0m;
                var currency = "ZAR";

                return Task.FromResult<WebhookEvent?>(status switch
                {
                    PaymentStatus.Completed => new ChargeSucceededEvent
                    {
                        GatewayReference = reference,
                        Status = status,
                        EventType = "ozow.notification",
                        Category = WebhookEventCategory.ChargeSucceeded,
                        Amount = amount,
                        Currency = currency
                    },
                    PaymentStatus.Pending => new ChargePendingEvent
                    {
                        GatewayReference = reference,
                        Status = status,
                        EventType = "ozow.notification",
                        Category = WebhookEventCategory.ChargePending,
                        Amount = amount,
                        Currency = currency
                    },
                    PaymentStatus.Failed => new ChargeFailedEvent
                    {
                        GatewayReference = reference,
                        Status = status,
                        EventType = "ozow.notification",
                        Category = WebhookEventCategory.ChargeFailed,
                        Amount = amount,
                        Currency = currency,
                        FailureCode = webhookEvent.Status,
                        FailureMessage = webhookEvent.StatusMessage
                    },
                    PaymentStatus.Refunded => new RefundSucceededEvent
                    {
                        GatewayReference = reference,
                        Status = status,
                        EventType = "ozow.notification",
                        Category = WebhookEventCategory.RefundSucceeded,
                        RefundReference = reference,
                        Amount = amount,
                        Currency = currency,
                        IsPartial = false
                    },
                    _ => new WebhookEvent
                    {
                        GatewayReference = reference,
                        Status = status,
                        EventType = "ozow.notification",
                        Category = WebhookEventCategory.Unknown
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to parse Ozow webhook event");
                return Task.FromResult<WebhookEvent?>(null);
            }
        }, ct);
    }

    /// <summary>
    /// Server-side call against api.ozow.com (transaction status / refund). The customer charge does NOT
    /// use this — it is a redirect. <paramref name="body"/> may be null for GET.
    /// </summary>
    private async Task<string> SendApiAsync(HttpMethod method, string path, object? body, CancellationToken ct, string operation)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Ozow failed", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Ozow timed out", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Ozow {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    /// <summary>
    /// SHA-512 of the UTF-8 bytes of <paramref name="input"/>, returned as lowercase hex. This does NOT
    /// case-fold the input — callers that need Ozow's "lowercase the concatenated string" rule must
    /// lowercase <paramref name="input"/> themselves (the charge does; the webhook hashes the payload as-is).
    /// </summary>
    private static string GenerateSha512HashHex(string input)
    {
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Truncate(string value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "complete" or "completed" => PaymentStatus.Completed,
        "pending" or "pendinginvestigation" => PaymentStatus.Pending,
        "cancelled" or "canceled" or "abandoned" => PaymentStatus.Cancelled,
        "error" or "failed" => PaymentStatus.Failed,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Ozow API response shapes (internal) ===

    private sealed class OzowRefundResponse
    {
        [JsonPropertyName("refundId")] public string? RefundId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class OzowWebhookNotification
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("transactionReference")] public string? TransactionReference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal? Amount { get; set; }
        [JsonPropertyName("statusMessage")] public string? StatusMessage { get; set; }
        [JsonPropertyName("hash")] public string? Hash { get; set; }
    }
}
