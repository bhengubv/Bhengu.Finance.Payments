// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Bhengu.Finance.Payments.PayFast.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayFast.Providers;

/// <summary>
/// PayFast implementation of <see cref="ISubscriptionProvider"/>.
/// </summary>
/// <remarks>
/// <para>PayFast has no first-class <em>Plan</em> resource. <see cref="CreatePlanAsync"/> stores the
/// plan template in a process-local <see cref="PayFastPlanCache"/> and returns the generated
/// reference. Each subscription created from the plan inlines those parameters onto the redirect
/// form-post.</para>
/// <para><see cref="CreateSubscriptionAsync"/> returns a <see cref="Subscription"/> in
/// <see cref="SubscriptionStatus.Pending"/>-equivalent state (<see cref="SubscriptionStatus.Trialing"/>
/// is the closest fit because no charge has occurred yet) carrying an <em>AuthorisationUrl</em> the
/// caller redirects the payer to. The real subscription token arrives later via the IPN webhook
/// (<c>token</c> field). See <see cref="PayFastPaymentProvider.ParseWebhookAsync"/> for the typed
/// event mapping.</para>
/// <para>Cancel / Pause / Resume / Get call PayFast's authenticated REST API at
/// <c>subscriptions/{token}/cancel</c>, <c>/pause</c>, <c>/unpause</c>, and <c>/fetch</c> respectively.
/// All these accept the subscription token (NOT the m_payment_id) — that's the token returned by
/// PayFast on the IPN, persisted by the merchant, and supplied as <c>subscriptionReference</c> here.</para>
/// </remarks>
public sealed class PayFastSubscriptionProvider : ISubscriptionProvider
{
    private readonly HttpClient _httpClient;
    private readonly PayFastOptions _options;
    private readonly ILogger<PayFastSubscriptionProvider> _logger;
    private readonly PayFastPlanCache _planCache;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.PayFast;

    /// <summary>Construct a PayFast subscription provider. Designed to be registered via DI.</summary>
    public PayFastSubscriptionProvider(
        HttpClient httpClient,
        IOptions<PayFastOptions> options,
        ILogger<PayFastSubscriptionProvider> logger,
        PayFastPlanCache planCache)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _planCache = planCache ?? throw new ArgumentNullException(nameof(planCache));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayFastOptions.MerchantId)} is required");

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(_options.UseSandbox
                ? "https://sandbox.payfast.co.za/"
                : "https://api.payfast.co.za/");
        }
    }

    /// <inheritdoc/>
    public Task<Plan> CreatePlanAsync(PlanRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = _planCache.Add(request);
        _logger.LogInformation("PayFast plan registered in-memory: {Reference} {Name} amount={Amount} {Currency} interval={Interval}",
            plan.Reference, plan.Name, plan.Amount, plan.Currency, plan.Interval);
        return Task.FromResult(plan);
    }

    /// <inheritdoc/>
    public Task<Plan?> GetPlanAsync(string planReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(planReference);
        return Task.FromResult(_planCache.Get(planReference));
    }

    /// <inheritdoc/>
    public Task<Subscription> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plan = _planCache.Get(request.PlanReference)
            ?? throw new BhenguPaymentException(ProviderName,
                $"PayFast plan '{request.PlanReference}' not found in plan cache. Call CreatePlanAsync first.",
                "plan_not_found");

        var mPaymentId = request.IdempotencyKey ?? $"sub-{Guid.NewGuid():N}";
        var startDate = request.StartAt ?? DateTime.UtcNow.AddDays(request.TrialDays ?? 0);

        var formData = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["merchant_id"] = _options.MerchantId,
            ["merchant_key"] = _options.MerchantKey,
            ["return_url"] = _options.ReturnUrl ?? string.Empty,
            ["cancel_url"] = _options.CancelUrl ?? string.Empty,
            ["notify_url"] = _options.NotifyUrl ?? string.Empty,
            ["m_payment_id"] = mPaymentId,
            ["amount"] = plan.Amount.ToString("F2", CultureInfo.InvariantCulture),
            ["item_name"] = plan.Name,
            ["item_description"] = plan.Description ?? string.Empty,
            ["currency"] = plan.Currency,
            ["custom_str1"] = request.CustomerId,
            ["custom_str2"] = plan.Reference,
            ["subscription_type"] = "1",
            ["billing_date"] = startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["recurring_amount"] = plan.Amount.ToString("F2", CultureInfo.InvariantCulture),
            ["frequency"] = MapFrequency(plan.Interval).ToString(CultureInfo.InvariantCulture),
            ["cycles"] = (plan.TotalCycles ?? 0).ToString(CultureInfo.InvariantCulture)
        };

        var signature = PayFastSignatureHelper.ComputeRedirectSignature(formData, _options.Passphrase ?? string.Empty);

        var qsParts = formData
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{kv.Key}={WebUtility.UrlEncode(kv.Value.Trim())}")
            .ToList();
        qsParts.Add($"signature={signature}");
        var authorisationUrl = $"{GetRedirectBaseUrl()}/eng/process?{string.Join("&", qsParts)}";

        var initialStatus = request.TrialDays is > 0 ? SubscriptionStatus.Trialing : SubscriptionStatus.Active;
        var subscription = new Subscription
        {
            Reference = mPaymentId, // until the IPN delivers the real token, callers correlate by m_payment_id
            PlanReference = plan.Reference,
            CustomerId = request.CustomerId,
            Status = initialStatus,
            StartedAt = startDate,
            NextBillingAt = startDate,
            CyclesCompleted = 0
        };

        // PayFast is a redirect-flow provider — the AuthorisationUrl is where the consumer
        // sends the payer to authorise the subscription. The current Core Subscription record
        // does not expose an AuthorisationUrl property; we surface the URL via the logger and
        // make it retrievable via TryGetAuthorisationUrl() for callers that need it.
        _lastAuthorisationUrl[mPaymentId] = authorisationUrl;
        _logger.LogInformation(
            "PayFast subscription redirect prepared: m_payment_id={MPaymentId} plan={PlanReference} amount={Amount} authorisationUrl={Url}",
            mPaymentId, plan.Reference, plan.Amount, authorisationUrl);

        return Task.FromResult(subscription);
    }

    /// <summary>
    /// Retrieve the PayFast redirect URL associated with the m_payment_id returned by
    /// <see cref="CreateSubscriptionAsync"/>. The mapping is held in process memory only.
    /// </summary>
    /// <param name="subscriptionReference">The subscription reference returned on creation (the m_payment_id).</param>
    /// <returns>The hosted-checkout redirect URL, or <c>null</c> if not known.</returns>
    public string? TryGetAuthorisationUrl(string subscriptionReference)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        return _lastAuthorisationUrl.TryGetValue(subscriptionReference, out var url) ? url : null;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastAuthorisationUrl =
        new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public async Task<Subscription?> GetSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        var fetched = await SendSignedAsync<PayFastSubscriptionFetchResponse>(
            HttpMethod.Get, $"subscriptions/{Uri.EscapeDataString(subscriptionReference)}/fetch", new Dictionary<string, string>(), ct)
            .ConfigureAwait(false);

        if (fetched?.Data is null)
            return null;

        return new Subscription
        {
            Reference = subscriptionReference,
            PlanReference = fetched.Data.CustomStr2 ?? string.Empty,
            CustomerId = fetched.Data.CustomStr1 ?? string.Empty,
            Status = MapSubscriptionStatus(fetched.Data.Status),
            StartedAt = fetched.Data.RunDate ?? DateTime.UtcNow,
            NextBillingAt = fetched.Data.RunDate,
            CyclesCompleted = fetched.Data.CyclesComplete ?? 0
        };
    }

    /// <inheritdoc/>
    public async Task<Subscription> CancelSubscriptionAsync(string subscriptionReference, bool immediately = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);

        try
        {
            await SendSignedAsync<object>(
                HttpMethod.Put, $"subscriptions/{Uri.EscapeDataString(subscriptionReference)}/cancel", new Dictionary<string, string>(), ct)
                .ConfigureAwait(false);
            _logger.LogInformation("PayFast subscription cancelled: {Reference}", subscriptionReference);
        }
        catch (PaymentDeclinedException ex) when (
            ex.ProviderErrorMessage?.Contains("already", StringComparison.OrdinalIgnoreCase) == true ||
            ex.ProviderErrorMessage?.Contains("cancel", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Idempotent: cancelling an already-cancelled subscription is success.
            _logger.LogInformation("PayFast subscription already cancelled (idempotent): {Reference}", subscriptionReference);
        }

        var existing = await SafeGetAsync(subscriptionReference, ct).ConfigureAwait(false);
        return existing with
        {
            Status = SubscriptionStatus.Cancelled,
            CancelledAt = DateTime.UtcNow,
            NextBillingAt = null
        };
    }

    /// <inheritdoc/>
    public async Task<Subscription> PauseSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        await SendSignedAsync<object>(
            HttpMethod.Put, $"subscriptions/{Uri.EscapeDataString(subscriptionReference)}/pause", new Dictionary<string, string>(), ct)
            .ConfigureAwait(false);
        _logger.LogInformation("PayFast subscription paused: {Reference}", subscriptionReference);
        var existing = await SafeGetAsync(subscriptionReference, ct).ConfigureAwait(false);
        return existing with { Status = SubscriptionStatus.Paused };
    }

    /// <inheritdoc/>
    public async Task<Subscription> ResumeSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        await SendSignedAsync<object>(
            HttpMethod.Put, $"subscriptions/{Uri.EscapeDataString(subscriptionReference)}/unpause", new Dictionary<string, string>(), ct)
            .ConfigureAwait(false);
        _logger.LogInformation("PayFast subscription resumed: {Reference}", subscriptionReference);
        var existing = await SafeGetAsync(subscriptionReference, ct).ConfigureAwait(false);
        return existing with { Status = SubscriptionStatus.Active };
    }

    private async Task<Subscription> SafeGetAsync(string subscriptionReference, CancellationToken ct)
    {
        try
        {
            var existing = await GetSubscriptionAsync(subscriptionReference, ct).ConfigureAwait(false);
            if (existing is not null) return existing;
        }
        catch (BhenguPaymentException)
        {
            // fall through to placeholder
        }

        return new Subscription
        {
            Reference = subscriptionReference,
            PlanReference = string.Empty,
            CustomerId = string.Empty,
            Status = SubscriptionStatus.Active,
            StartedAt = DateTime.UtcNow,
            CyclesCompleted = 0
        };
    }

    private async Task<T?> SendSignedAsync<T>(
        HttpMethod method,
        string relativePath,
        IDictionary<string, string> bodyParams,
        CancellationToken ct) where T : class
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var signature = PayFastSignatureHelper.ComputeApiSignature(
            _options.MerchantId,
            _options.Passphrase ?? string.Empty,
            timestamp,
            bodyParams);

        var url = relativePath + (_options.UseSandbox
            ? (relativePath.Contains('?', StringComparison.Ordinal) ? "&testing=true" : "?testing=true")
            : string.Empty);

        using var req = new HttpRequestMessage(method, url);
        req.Headers.Add("merchant-id", _options.MerchantId);
        req.Headers.Add("version", "v1");
        req.Headers.Add("timestamp", timestamp);
        req.Headers.Add("signature", signature);

        if (bodyParams.Count > 0 && method != HttpMethod.Get)
            req.Content = new FormUrlEncodedContent(bodyParams);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "PayFast API HTTP failure", ex);
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, body);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("PayFast {Method} {Path} failed: {Status} {Body}", method, relativePath, response.StatusCode, body);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), body);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {body}");
        }

        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(body);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "PayFast {Method} {Path} returned non-JSON body — accepting as ack", method, relativePath);
            return null;
        }
    }

    private string GetRedirectBaseUrl() => _options.UseSandbox
        ? (_options.SandboxUrl ?? "https://sandbox.payfast.co.za")
        : (_options.BaseUrl ?? "https://www.payfast.co.za");

    private static int MapFrequency(SubscriptionInterval interval) => interval switch
    {
        SubscriptionInterval.Daily => 3,        // PayFast has no daily — closest is monthly = 3
        SubscriptionInterval.Weekly => 3,       // ditto
        SubscriptionInterval.BiWeekly => 3,     // ditto
        SubscriptionInterval.Monthly => 3,
        SubscriptionInterval.Quarterly => 4,
        SubscriptionInterval.BiAnnually => 5,
        SubscriptionInterval.Annually => 6,
        _ => 3
    };

    private static SubscriptionStatus MapSubscriptionStatus(int? status) => status switch
    {
        1 => SubscriptionStatus.Active,
        2 => SubscriptionStatus.Cancelled,
        3 => SubscriptionStatus.Paused,
        4 => SubscriptionStatus.Expired,
        _ => SubscriptionStatus.Active
    };

    // === PayFast API shapes (internal) ===

    private sealed class PayFastSubscriptionFetchResponse
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("data")] public PayFastSubscriptionData? Data { get; set; }
    }

    private sealed class PayFastSubscriptionData
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("cycles")] public int? Cycles { get; set; }
        [JsonPropertyName("cycles_complete")] public int? CyclesComplete { get; set; }
        [JsonPropertyName("frequency")] public int? Frequency { get; set; }
        [JsonPropertyName("status")] public int? Status { get; set; }
        [JsonPropertyName("status_text")] public string? StatusText { get; set; }
        [JsonPropertyName("status_reason")] public string? StatusReason { get; set; }
        [JsonPropertyName("run_date")] public DateTime? RunDate { get; set; }
        [JsonPropertyName("custom_str1")] public string? CustomStr1 { get; set; }
        [JsonPropertyName("custom_str2")] public string? CustomStr2 { get; set; }
    }
}
