// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
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
using Bhengu.Finance.Payments.PayFast.Configuration;
using Bhengu.Finance.Payments.PayFast.Internals;
using Bhengu.Finance.Payments.PayFast.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayFast.Providers;

/// <summary>
/// PayFast (South Africa) payment gateway provider.
/// Supports tokenised ad-hoc charging via the PayFast subscriptions API.
/// PayFast does NOT support payouts via API — <see cref="IPayoutProvider"/> is intentionally not implemented.
/// </summary>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "Wire format built from public documentation; never sandbox-verified.")]
public sealed class PayFastPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider
{
    private const string SubSeenKeyPrefix = "payfast:sub-seen:";
    private static readonly TimeSpan SubSeenTtl = TimeSpan.FromDays(90);

    private readonly HttpClient _httpClient;
    private readonly PayFastOptions _options;
    private readonly IBhenguDistributedCache _cache;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.PayFast;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.TypedWebhooks |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.Subscriptions |
        ProviderCapabilities.Mandates;

    /// <summary>
    /// Construct the provider. The <paramref name="cache"/> backs the subscription "first-seen"
    /// dedup used to distinguish SubscriptionCreated from SubscriptionRenewed across replicas and
    /// process restarts (entries are TTL'd to 90 days).
    /// </summary>
    public PayFastPaymentProvider(
        HttpClient httpClient,
        IOptions<PayFastOptions> options,
        ILogger<PayFastPaymentProvider> logger,
        IBhenguDistributedCache cache)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayFastOptions.MerchantId)} is required");

        if (_httpClient.BaseAddress is null)
        {
            // PayFast's REST API is ALWAYS served from api.payfast.co.za. Sandbox is selected per-request
            // via the "?testing=true" query suffix (see ProcessPaymentCoreAsync), NOT a different host —
            // sandbox.payfast.co.za only serves the /eng/process & /onsite/process browser-redirect flows.
            _httpClient.BaseAddress = new Uri("https://api.payfast.co.za/");
        }
    }

    /// <summary>Back-compat constructor that uses the process-local in-memory cache.</summary>
    public PayFastPaymentProvider(
        HttpClient httpClient,
        IOptions<PayFastOptions> options,
        ILogger<PayFastPaymentProvider> logger)
        : this(httpClient, options, logger, new InMemoryBhenguDistributedCache())
    {
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, () => ProcessPaymentCoreAsync(request, ct), ct);
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
        var amountInCents = (int)(request.Amount * 100);
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");

        var formData = new Dictionary<string, string>
        {
            ["amount"] = amountInCents.ToString(),
            ["item_name"] = request.Description
        };

        if (request.Metadata is not null)
        {
            if (request.Metadata.TryGetValue("payment_id", out var paymentId))
                formData["m_payment_id"] = paymentId;
            else if (request.Metadata.TryGetValue("transaction_id", out var transactionId))
                formData["m_payment_id"] = transactionId;

            // Optional CVV re-check for the ad-hoc (tokenised) charge — passed through when the caller supplies it.
            if (request.Metadata.TryGetValue("cc_cvv", out var ccCvv) && !string.IsNullOrEmpty(ccCvv))
                formData["cc_cvv"] = ccCvv;
        }

        var signature = PayFastSignatureHelper.ComputeApiSignature(
            _options.MerchantId,
            _options.Passphrase ?? string.Empty,
            timestamp,
            formData);

        var path = $"subscriptions/{Uri.EscapeDataString(request.PaymentMethodToken)}/adhoc{(_options.UseSandbox ? "?testing=true" : "")}";

        using var http = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(formData)
        };
        http.Headers.Add("merchant-id", _options.MerchantId);
        http.Headers.Add("version", "v1");
        http.Headers.Add("timestamp", timestamp);
        http.Headers.Add("signature", signature);

        var response = await _httpClient.SendAsync(http, ct).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, body);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("PayFast payment failed: {StatusCode} {Body}", response.StatusCode, body);
            // 4xx that isn't 429 — treat as a decline (insufficient funds, card error, etc.)
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), body);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {body}");
        }

        var payfastResponse = JsonSerializer.Deserialize<PayFastAdhocResponse>(body);
        var status = MapStatus(payfastResponse?.data?.response ?? "pending");

        Logger.LogInformation("PayFast ad-hoc payment created: {GatewayReference} status={Status}",
            payfastResponse?.data?.pf_payment_id, status);

        return new PaymentResponse
        {
            GatewayReference = payfastResponse?.data?.pf_payment_id ?? string.Empty,
            Status = status,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = payfastResponse?.data?.response_reason
        };
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunRefundAsync(request.GatewayReference, () => ProcessRefundCoreAsync(request, ct), ct);
    }

    private async Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
        // PayFast refunds run against the authenticated REST API: POST /refunds/{pf_payment_id}.
        // The refund endpoint is NOT available in sandbox — PayFast only processes refunds in production.
        if (_options.UseSandbox)
            throw new BhenguPaymentException(ProviderName,
                "PayFast refunds are not available in sandbox mode — PayFast only processes refunds in production.",
                "refund_sandbox_unsupported");

        // REST money values are in CENTS (integer).
        var amountInCents = (long)Math.Round(request.Amount * 100m, MidpointRounding.AwayFromZero);
        var body = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["amount"] = amountInCents.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["reason"] = request.Reason,
            ["notify_buyer"] = "1"
        };

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");
        var signature = PayFastSignatureHelper.ComputeApiSignature(
            _options.MerchantId, _options.Passphrase ?? string.Empty, timestamp, body);

        using var http = new HttpRequestMessage(HttpMethod.Post, $"refunds/{Uri.EscapeDataString(request.GatewayReference)}")
        {
            Content = new FormUrlEncodedContent(body)
        };
        http.Headers.Add("merchant-id", _options.MerchantId);
        http.Headers.Add("version", "v1");
        http.Headers.Add("timestamp", timestamp);
        http.Headers.Add("signature", signature);

        var response = await _httpClient.SendAsync(http, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("PayFast refund failed for {GatewayReference}: {Status} {Body}",
                request.GatewayReference, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        Logger.LogInformation("PayFast refund submitted for {GatewayReference} amount={Amount}", request.GatewayReference, request.Amount);

        return new RefundResponse
        {
            // PayFast correlates the refund to the original transaction's pf_payment_id.
            GatewayReference = request.GatewayReference,
            Amount = request.Amount,
            Status = PaymentStatus.Pending, // accepted by PayFast; settlement completes asynchronously
            ProcessedAt = DateTime.UtcNow,
            Message = "Refund submitted to PayFast."
        };
    }

    /// <summary>
    /// Fetch a PayFast refund's current status by the original transaction's <c>pf_payment_id</c>. Returns
    /// PayFast's raw JSON response (PayFast's refund schema is not part of this SDK's typed surface, so the
    /// caller parses it). Like refund creation, this is NOT available in sandbox.
    /// </summary>
    public Task<string> FetchRefundAsync(string pfPaymentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(pfPaymentId);
        return RunOperationAsync("fetch_refund", () => FetchRefundCoreAsync(pfPaymentId, ct), ct);
    }

    private async Task<string> FetchRefundCoreAsync(string pfPaymentId, CancellationToken ct)
    {
        if (_options.UseSandbox)
            throw new BhenguPaymentException(ProviderName,
                "PayFast refunds are not available in sandbox mode — PayFast only processes refunds in production.",
                "refund_sandbox_unsupported");

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");
        var signature = PayFastSignatureHelper.ComputeApiSignature(
            _options.MerchantId, _options.Passphrase ?? string.Empty, timestamp, new Dictionary<string, string>());

        using var req = new HttpRequestMessage(HttpMethod.Get, $"refunds/{Uri.EscapeDataString(pfPaymentId)}");
        req.Headers.Add("merchant-id", _options.MerchantId);
        req.Headers.Add("version", "v1");
        req.Headers.Add("timestamp", timestamp);
        req.Headers.Add("signature", signature);

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("PayFast refund fetch failed for {GatewayReference}: {Status} {Body}", pfPaymentId, response.StatusCode, body);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), body);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {body}");
        }

        return body;
    }

    /// <summary>
    /// Verifies a PayFast ITN webhook signature using MD5 of alphabetically-sorted parameters + passphrase.
    /// </summary>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        return RunWebhookVerify(() =>
        {
            // PayFast ITN signature: hash the posted fields IN THE ORDER PayFast POSTED THEM (NOT sorted),
            // stopping at the 'signature' field, then append the passphrase. This is PayFast's official
            // Notification::dataToString algorithm — and it differs from the REST API signer, which DOES
            // sort alphabetically. (Sorting the ITN, as this code used to, can reject a genuine notification.)
            var parts = new List<string>();
            foreach (var pair in payload.Split('&'))
            {
                var kv = pair.Split('=', 2);
                var key = WebUtility.UrlDecode(kv[0]);
                if (key == "signature")
                    break;
                var value = kv.Length == 2 ? WebUtility.UrlDecode(kv[1]) : string.Empty;
                parts.Add($"{key}={WebUtility.UrlEncode(value)}");
            }

            var canonical = string.Join("&", parts);
            if (!string.IsNullOrEmpty(_options.Passphrase))
                canonical += $"&passphrase={WebUtility.UrlEncode(_options.Passphrase)}";

            return SignatureHelpers.VerifyMd5(canonical, signature);
        });
    }

    /// <summary>
    /// Fully validate a PayFast ITN (Instant Transaction Notification) before treating it as a settled
    /// payment. Runs PayFast's four-step verification — and you MUST run all four; a valid signature
    /// alone does not prove a real payment:
    /// <list type="number">
    ///   <item>Signature — the ITN's MD5 signature matches (same algorithm as <see cref="VerifyWebhookSignature"/>).</item>
    ///   <item>Source — when <paramref name="sourceIp"/> is supplied, it must resolve to one of PayFast's
    ///         published ITN hosts (<see cref="PayFastOptions.ValidItnHosts"/>).</item>
    ///   <item>Server confirm — the raw ITN is POSTed back to PayFast's <c>/eng/query/validate</c> and must
    ///         return <c>VALID</c>. This is the step that defeats replayed or forged notifications.</item>
    ///   <item>Amount — when <paramref name="expectedAmount"/> is supplied, the ITN's <c>amount_gross</c>
    ///         must equal it (guards against a tampered amount).</item>
    /// </list>
    /// Only treat the payment as settled when <see cref="PayFastItnValidationResult.IsValid"/> is true.
    /// </summary>
    /// <param name="payload">The raw <c>application/x-www-form-urlencoded</c> ITN body exactly as PayFast posted it.</param>
    /// <param name="sourceIp">The remote IP the ITN POST came from (e.g. <c>HttpContext.Connection.RemoteIpAddress</c>).
    /// When null/empty the source gate is skipped (reported as not-checked; does not fail validation).</param>
    /// <param name="expectedAmount">The amount the order is expected to cost. When null the amount gate is skipped.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PayFastItnValidationResult> ValidateItnAsync(
        string payload,
        string? sourceIp = null,
        decimal? expectedAmount = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        var parameters = ParseFormUrlEncoded(payload);
        var pfPaymentId = parameters.GetValueOrDefault("pf_payment_id");
        var mPaymentId = parameters.GetValueOrDefault("m_payment_id");
        var paymentStatus = parameters.GetValueOrDefault("payment_status");
        var amountGross = decimal.TryParse(parameters.GetValueOrDefault("amount_gross", "0"),
            System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var ag) ? ag : 0m;

        PayFastItnValidationResult Fail(string reason, bool sig = false, bool? src = null, bool confirmed = false, bool? amt = null) =>
            new()
            {
                IsValid = false,
                Reason = reason,
                SignatureValid = sig,
                SourceValid = src,
                ServerConfirmed = confirmed,
                AmountMatched = amt,
                PfPaymentId = pfPaymentId,
                MPaymentId = mPaymentId,
                PaymentStatus = paymentStatus,
                AmountGross = amountGross
            };

        // Gate 1 — signature (alphabetical-sort MD5, matching the production PayfastAPI implementation).
        var signature = parameters.GetValueOrDefault("signature", string.Empty);
        if (string.IsNullOrEmpty(signature) || !VerifyWebhookSignature(payload, signature))
            return Fail("ITN signature verification failed");

        // Gate 2 — source IP (only when the caller supplies one).
        bool? sourceValid = null;
        if (!string.IsNullOrWhiteSpace(sourceIp))
        {
            sourceValid = await IsValidPayFastSourceAsync(sourceIp!, ct).ConfigureAwait(false);
            if (sourceValid == false)
                return Fail($"ITN source IP '{sourceIp}' is not a known PayFast host", sig: true, src: false);
        }

        // Gate 3 — PayFast server confirmation (REQUIRED). Defeats replay/forgery.
        var confirmed = await ConfirmItnWithPayFastAsync(payload, ct).ConfigureAwait(false);
        if (!confirmed)
            return Fail("PayFast did not confirm the ITN as VALID", sig: true, src: sourceValid);

        // Gate 4 — amount reconciliation (only when the caller supplies an expected amount).
        bool? amountMatched = null;
        if (expectedAmount is { } expected)
        {
            amountMatched = amountGross == expected;
            if (amountMatched == false)
                return Fail($"ITN amount_gross {amountGross} does not match expected {expected}",
                    sig: true, src: sourceValid, confirmed: true, amt: false);
        }

        return new PayFastItnValidationResult
        {
            IsValid = true,
            Reason = "valid",
            SignatureValid = true,
            SourceValid = sourceValid,
            ServerConfirmed = true,
            AmountMatched = amountMatched,
            PfPaymentId = pfPaymentId,
            MPaymentId = mPaymentId,
            PaymentStatus = paymentStatus,
            AmountGross = amountGross
        };
    }

    /// <summary>
    /// Resolve PayFast's published ITN hosts (<see cref="PayFastOptions.ValidItnHosts"/>) and confirm
    /// <paramref name="sourceIp"/> is one of their addresses.
    /// </summary>
    private async Task<bool> IsValidPayFastSourceAsync(string sourceIp, CancellationToken ct)
    {
        if (!IPAddress.TryParse(sourceIp, out var remote))
        {
            Logger.LogWarning("PayFast ITN source '{SourceIp}' is not a valid IP address", sourceIp);
            return false;
        }

        foreach (var host in _options.ValidItnHosts)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                if (addresses.Any(a => a.Equals(remote)))
                    return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "Failed to resolve PayFast ITN host {Host} during source validation", host);
            }
        }

        return false;
    }

    /// <summary>
    /// POST the raw ITN back to PayFast's <c>/eng/query/validate</c> endpoint. PayFast replies with the
    /// literal text <c>VALID</c> (or <c>INVALID</c>). The validate host is the website host
    /// (www / sandbox), NOT the REST API host.
    /// </summary>
    private async Task<bool> ConfirmItnWithPayFastAsync(string payload, CancellationToken ct)
    {
        try
        {
            var host = (_options.UseSandbox
                ? (_options.SandboxUrl ?? "https://sandbox.payfast.co.za")
                : (_options.BaseUrl ?? "https://www.payfast.co.za")).TrimEnd('/');

            // Absolute URI — HttpClient ignores BaseAddress (which points at the REST api host) when the
            // request URI is absolute, so this correctly hits the website host.
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{host}/eng/query/validate") { Content = content };

            var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var body = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();

            if (!resp.IsSuccessStatusCode)
            {
                Logger.LogError("PayFast ITN validate returned {Status}: {Body}", resp.StatusCode, body);
                return false;
            }

            // PayFast returns "VALID" on the first line when the ITN is genuine.
            return body.StartsWith("VALID", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "PayFast ITN server confirmation failed");
            return false;
        }
    }

    /// <summary>
    /// Parse a PayFast ITN payload into a typed <see cref="WebhookEvent"/> sub-record.
    /// </summary>
    /// <remarks>
    /// Mapping rules (PayFast IPN <c>payment_status</c>):
    /// <list type="bullet">
    /// <item><description><c>COMPLETE</c> + token field present (first time) → <see cref="SubscriptionCreatedEvent"/>.</description></item>
    /// <item><description><c>COMPLETE</c> + token field present (seen before) → <see cref="SubscriptionRenewedEvent"/>.</description></item>
    /// <item><description><c>COMPLETE</c> without token → <see cref="ChargeSucceededEvent"/>.</description></item>
    /// <item><description><c>FAILED</c> → <see cref="ChargeFailedEvent"/>.</description></item>
    /// <item><description><c>PENDING</c> → <see cref="ChargePendingEvent"/>.</description></item>
    /// <item><description><c>CANCELLED</c> + token field present → <see cref="SubscriptionCancelledEvent"/>.</description></item>
    /// </list>
    /// Subscription-token dedup is tracked in a process-local set; for production deployments
    /// (multi-instance, restart-safe), wrap with an external idempotency layer keyed on token+pf_payment_id.
    /// </remarks>
    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return RunOperationAsync<WebhookEvent?>("parse_webhook", () => ParseWebhookCoreAsync(payload, ct), ct);
    }

    private async Task<WebhookEvent?> ParseWebhookCoreAsync(string payload, CancellationToken ct)
    {
        try
        {
            var parameters = ParseFormUrlEncoded(payload);

            var pfPaymentId = parameters.GetValueOrDefault("pf_payment_id", string.Empty);
            var paymentStatus = parameters.GetValueOrDefault("payment_status", string.Empty);
            var token = parameters.GetValueOrDefault("token", string.Empty);
            var amountGross = decimal.TryParse(parameters.GetValueOrDefault("amount_gross", "0"),
                System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var ag) ? ag : 0m;
            var status = MapStatus(paymentStatus);
            var hasToken = !string.IsNullOrEmpty(token);

            Logger.LogInformation("PayFast ITN parsed: gatewayReference={PfPaymentId} status={Status} hasToken={HasToken}",
                pfPaymentId, status, hasToken);

            // For COMPLETE+token (subscription), decide created-vs-renewed by checking a distributed
            // dedup record. First sighting → Created; subsequent → Renewed. 90d TTL bounds growth.
            var isFirstSeen = false;
            if (string.Equals(paymentStatus, "COMPLETE", StringComparison.OrdinalIgnoreCase) && hasToken)
            {
                var key = SubSeenKeyPrefix + token;
                var prior = await _cache.GetAsync<TokenMarker>(key, ct).ConfigureAwait(false);
                if (prior is null)
                {
                    await _cache.SetAsync(key, new TokenMarker(token), SubSeenTtl, ct).ConfigureAwait(false);
                    isFirstSeen = true;
                }
            }

            // Typed sub-records by (payment_status, hasToken).
            WebhookEvent typed = (paymentStatus.ToUpperInvariant(), hasToken) switch
            {
                ("COMPLETE", true) when isFirstSeen => new SubscriptionCreatedEvent
                {
                    GatewayReference = pfPaymentId,
                    Status = status,
                    EventType = "payfast.itn.subscription.created",
                    Category = WebhookEventCategory.SubscriptionCreated,
                    RawPayload = parameters,
                    SubscriptionReference = token,
                    PlanReference = parameters.GetValueOrDefault("custom_str2", string.Empty),
                    CustomerId = parameters.GetValueOrDefault("custom_str1")
                },
                ("COMPLETE", true) => new SubscriptionRenewedEvent
                {
                    GatewayReference = pfPaymentId,
                    Status = status,
                    EventType = "payfast.itn.subscription.renewed",
                    Category = WebhookEventCategory.SubscriptionRenewed,
                    RawPayload = parameters,
                    SubscriptionReference = token,
                    Amount = amountGross,
                    Currency = parameters.GetValueOrDefault("amount_currency", "ZAR")
                },
                ("COMPLETE", false) => new ChargeSucceededEvent
                {
                    GatewayReference = pfPaymentId,
                    Status = status,
                    EventType = "payfast.itn",
                    Category = WebhookEventCategory.ChargeSucceeded,
                    RawPayload = parameters,
                    Amount = amountGross,
                    Currency = parameters.GetValueOrDefault("amount_currency", "ZAR"),
                    CustomerId = parameters.GetValueOrDefault("custom_str1"),
                    PaymentMethodToken = null
                },
                ("FAILED", _) => new ChargeFailedEvent
                {
                    GatewayReference = pfPaymentId,
                    Status = status,
                    EventType = "payfast.itn",
                    Category = WebhookEventCategory.ChargeFailed,
                    RawPayload = parameters,
                    Amount = amountGross,
                    Currency = parameters.GetValueOrDefault("amount_currency", "ZAR"),
                    FailureCode = parameters.GetValueOrDefault("reason_code"),
                    FailureMessage = parameters.GetValueOrDefault("reason")
                },
                ("PENDING", _) => new ChargePendingEvent
                {
                    GatewayReference = pfPaymentId,
                    Status = status,
                    EventType = "payfast.itn",
                    Category = WebhookEventCategory.ChargePending,
                    RawPayload = parameters,
                    Amount = amountGross,
                    Currency = parameters.GetValueOrDefault("amount_currency", "ZAR")
                },
                ("CANCELLED" or "CANCELED", true) => new SubscriptionCancelledEvent
                {
                    GatewayReference = pfPaymentId,
                    Status = status,
                    EventType = "payfast.itn.subscription.cancelled",
                    Category = WebhookEventCategory.SubscriptionCancelled,
                    RawPayload = parameters,
                    SubscriptionReference = token,
                    CancellationReason = parameters.GetValueOrDefault("reason")
                },
                _ => new WebhookEvent
                {
                    GatewayReference = pfPaymentId,
                    Status = status,
                    EventType = "payfast.itn",
                    Category = WebhookEventCategory.Unknown,
                    RawPayload = parameters
                }
            };

            return typed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Failed to parse PayFast ITN payload");
            return null;
        }
    }

    /// <summary>Serialisable marker persisted in the distributed cache for subscription-seen dedup.</summary>
    private sealed record TokenMarker(string Token);

    // === PayFast-specific extensions (not on IPaymentGatewayProvider) ===

    /// <summary>Fetch details of a tokenisation agreement (ad-hoc subscription).</summary>
    public async Task<PayFastTokenInfo?> FetchTokenAsync(string token, CancellationToken ct = default)
    {
        return await SendSignedAsync<PayFastFetchResponse>(
            HttpMethod.Get, $"subscriptions/{token}/fetch", ct)
            .ConfigureAwait(false) is { } r ? r.data : null;
    }

    /// <summary>Cancel a tokenisation agreement.</summary>
    public async Task<bool> CancelTokenAsync(string token, CancellationToken ct = default)
    {
        try
        {
            await SendSignedAsync<object>(HttpMethod.Put, $"subscriptions/{token}/cancel", ct).ConfigureAwait(false);
            Logger.LogInformation("PayFast token cancelled: {Token}", token);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "PayFast cancel token failed for {Token}", token);
            return false;
        }
    }

    /// <summary>Query a transaction by ID.</summary>
    public Task<PayFastTransactionQuery?> QueryTransactionAsync(string transactionIdOrPaymentId, CancellationToken ct = default)
        => SendSignedAsync<PayFastTransactionQuery>(HttpMethod.Get, $"process/query/{transactionIdOrPaymentId}", ct);

    private async Task<T?> SendSignedAsync<T>(HttpMethod method, string relativePath, CancellationToken ct) where T : class
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");
        var signature = PayFastSignatureHelper.ComputeApiSignature(
            _options.MerchantId,
            _options.Passphrase ?? string.Empty,
            timestamp,
            new Dictionary<string, string>());

        var url = relativePath + (_options.UseSandbox ? (relativePath.Contains('?') ? "&testing=true" : "?testing=true") : "");
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Add("merchant-id", _options.MerchantId);
        req.Headers.Add("version", "v1");
        req.Headers.Add("timestamp", timestamp);
        req.Headers.Add("signature", signature);

        var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            Logger.LogError("PayFast {Method} {Path} failed: {Status} {Body}", method, relativePath, resp.StatusCode, body);
            return null;
        }
        return JsonSerializer.Deserialize<T>(body);
    }

    private static Dictionary<string, string> ParseFormUrlEncoded(string formData)
    {
        // PayFast ITN is application/x-www-form-urlencoded — '+' represents a literal space.
        // Uri.UnescapeDataString does NOT translate '+' → ' ', so we use WebUtility.UrlDecode
        // which does. This matches the de-facto IPN parsing every PayFast SDK ships with.
        var result = new Dictionary<string, string>();
        foreach (var pair in formData.Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2)
                result[WebUtility.UrlDecode(kv[0])] = WebUtility.UrlDecode(kv[1]);
        }
        return result;
    }

    private static PaymentStatus MapStatus(string raw) => raw.ToUpperInvariant() switch
    {
        "APPROVED" or "COMPLETE" or "COMPLETED" => PaymentStatus.Completed,
        "PENDING" => PaymentStatus.Pending,
        "FAILED" => PaymentStatus.Failed,
        "CANCELLED" or "CANCELED" => PaymentStatus.Cancelled,
        "REFUNDED" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === PayFast API response shapes (internal) ===

    private sealed class PayFastAdhocResponse
    {
        public int code { get; set; }
        public string? status { get; set; }
        public PayFastAdhocData? data { get; set; }
    }

    private sealed class PayFastAdhocData
    {
        public bool message { get; set; }
        public string? pf_payment_id { get; set; }
        public string? response { get; set; }
        public string? response_reason { get; set; }
    }

    private sealed class PayFastFetchResponse
    {
        public int code { get; set; }
        public string? status { get; set; }
        public PayFastTokenInfo? data { get; set; }
    }
}

/// <summary>PayFast tokenisation agreement details.</summary>
public sealed class PayFastTokenInfo
{
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("status_text")] public string? StatusText { get; set; }
    [JsonPropertyName("status_reason")] public string? StatusReason { get; set; }
}

/// <summary>PayFast transaction query response.</summary>
public sealed class PayFastTransactionQuery
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("data")] public PayFastTransactionData? Data { get; set; }
}

public sealed class PayFastTransactionData
{
    [JsonPropertyName("pf_payment_id")] public string? PfPaymentId { get; set; }
    [JsonPropertyName("payment_status")] public string? PaymentStatus { get; set; }
    [JsonPropertyName("amount_gross")] public decimal AmountGross { get; set; }
    [JsonPropertyName("amount_fee")] public decimal AmountFee { get; set; }
    [JsonPropertyName("amount_net")] public decimal AmountNet { get; set; }
}
