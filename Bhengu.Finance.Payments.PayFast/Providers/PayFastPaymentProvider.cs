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
            _httpClient.BaseAddress = new Uri(_options.UseSandbox
                ? "https://sandbox.payfast.co.za/"
                : "https://api.payfast.co.za/");
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

    private Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
        // PayFast does not expose a refund API — refunds are processed manually via merchant dashboard.
        // We return a deterministic tracking reference so the caller can match this entry to the manual
        // action when reconciling. Consumers requiring automated refunds must use a different provider.
        Logger.LogWarning(
            "PayFast refund requested for {GatewayReference} amount={Amount}. PayFast has no refund API; manual dashboard processing required.",
            request.GatewayReference, request.Amount);

        var trackingReference = $"PAYFAST-MANUAL-REFUND-{request.GatewayReference}-{DateTime.UtcNow:yyyyMMddHHmmss}";

        return Task.FromResult(new RefundResponse
        {
            GatewayReference = trackingReference,
            Amount = request.Amount,
            Status = PaymentStatus.Pending,
            ProcessedAt = DateTime.UtcNow,
            Message = "PayFast refunds require manual processing via the merchant dashboard."
        });
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
            var parameters = ParseFormUrlEncoded(payload);
            parameters.Remove("signature");

            var sorted = parameters
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key}={WebUtility.UrlEncode(p.Value)}")
                .ToList();

            if (!string.IsNullOrEmpty(_options.Passphrase))
                sorted.Add($"passphrase={WebUtility.UrlEncode(_options.Passphrase)}");

            var canonical = string.Join("&", sorted);
            return SignatureHelpers.VerifyMd5(canonical, signature);
        });
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
