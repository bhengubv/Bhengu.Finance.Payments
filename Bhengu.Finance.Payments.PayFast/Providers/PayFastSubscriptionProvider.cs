// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.Core.Providers;
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
/// is the closest fit because no charge has occurred yet) carrying an <see cref="Subscription.AuthorisationUrl"/>
/// the caller redirects the payer to. The real subscription token arrives later via the IPN webhook
/// (<c>token</c> field). See <see cref="PayFastPaymentProvider.ParseWebhookAsync"/> for the typed
/// event mapping.</para>
/// <para>Cancel / Pause / Resume / Get call PayFast's authenticated REST API at
/// <c>subscriptions/{token}/cancel</c>, <c>/pause</c>, <c>/unpause</c>, and <c>/fetch</c> respectively.
/// All these accept the subscription token (NOT the m_payment_id) — that's the token returned by
/// PayFast on the IPN, persisted by the merchant, and supplied as <c>subscriptionReference</c> here.</para>
/// <para>Pause / Resume are surfaced via the dedicated <see cref="ISubscriptionPauseSupport"/>
/// add-on contract so consumers can pattern-match support at compile time instead of catching
/// runtime "not supported" exceptions.</para>
/// </remarks>
public sealed class PayFastSubscriptionProvider : BhenguProviderBase, ISubscriptionProvider, ISubscriptionPauseSupport, ISubscriptionUpdateSupport
{
    private readonly HttpClient _httpClient;
    private readonly PayFastOptions _options;
    private readonly PayFastPlanCache _planCache;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.PayFast;

    /// <summary>Construct a PayFast subscription provider. Designed to be registered via DI.</summary>
    public PayFastSubscriptionProvider(
        HttpClient httpClient,
        IOptions<PayFastOptions> options,
        ILogger<PayFastSubscriptionProvider> logger,
        PayFastPlanCache planCache)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _planCache = planCache ?? throw new ArgumentNullException(nameof(planCache));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayFastOptions.MerchantId)} is required");

        if (_httpClient.BaseAddress is null)
        {
            // PayFast's REST API is ALWAYS served from api.payfast.co.za. Sandbox is selected per-request
            // via the "?testing=true" query suffix, NOT a different host — sandbox.payfast.co.za only
            // serves the /eng/process & /onsite/process browser-redirect flows.
            _httpClient.BaseAddress = new Uri("https://api.payfast.co.za/");
        }
    }

    /// <inheritdoc/>
    public Task<Plan> CreatePlanAsync(PlanRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunOperationAsync("create_plan", () =>
        {
            var plan = _planCache.Add(request);
            Logger.LogInformation("PayFast plan registered in-memory: {Reference} {Name} amount={Amount} {Currency} interval={Interval}",
                plan.Reference, plan.Name, plan.Amount, plan.Currency, plan.Interval);
            return Task.FromResult(plan);
        }, ct);
    }

    /// <inheritdoc/>
    public Task<Plan?> GetPlanAsync(string planReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(planReference);
        return RunOperationAsync("get_plan", () => Task.FromResult(_planCache.Get(planReference)), ct);
    }

    /// <inheritdoc/>
    public Task<Subscription> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunOperationAsync("create_subscription", () => CreateSubscriptionCoreAsync(request), ct);
    }

    private Task<Subscription> CreateSubscriptionCoreAsync(SubscriptionRequest request)
    {
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
            CyclesCompleted = 0,
            AuthorisationUrl = authorisationUrl
        };

        Logger.LogInformation(
            "PayFast subscription redirect prepared: m_payment_id={MPaymentId} plan={PlanReference} amount={Amount} authorisationUrl={Url}",
            mPaymentId, plan.Reference, plan.Amount, authorisationUrl);

        return Task.FromResult(subscription);
    }

    /// <inheritdoc/>
    public Task<Subscription?> GetSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        return RunOperationAsync("get_subscription", () => GetSubscriptionCoreAsync(subscriptionReference, ct), ct);
    }

    private async Task<Subscription?> GetSubscriptionCoreAsync(string subscriptionReference, CancellationToken ct)
    {
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
    public Task<Subscription> CancelSubscriptionAsync(string subscriptionReference, bool immediately = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        return RunOperationAsync("cancel_subscription", () => CancelSubscriptionCoreAsync(subscriptionReference, ct), ct);
    }

    private async Task<Subscription> CancelSubscriptionCoreAsync(string subscriptionReference, CancellationToken ct)
    {
        try
        {
            await SendSignedAsync<object>(
                HttpMethod.Put, $"subscriptions/{Uri.EscapeDataString(subscriptionReference)}/cancel", new Dictionary<string, string>(), ct)
                .ConfigureAwait(false);
            Logger.LogInformation("PayFast subscription cancelled: {Reference}", subscriptionReference);
        }
        catch (PaymentDeclinedException ex) when (
            ex.ProviderErrorMessage?.Contains("already", StringComparison.OrdinalIgnoreCase) == true ||
            ex.ProviderErrorMessage?.Contains("cancel", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Idempotent: cancelling an already-cancelled subscription is success.
            Logger.LogInformation("PayFast subscription already cancelled (idempotent): {Reference}", subscriptionReference);
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
    public Task<Subscription> PauseSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        return RunOperationAsync("pause_subscription", () => PauseSubscriptionCoreAsync(subscriptionReference, null, ct), ct);
    }

    /// <summary>
    /// Pause a subscription for a specific number of billing cycles — PayFast's optional pause duration
    /// (<c>cycles</c> on <c>PUT subscriptions/{token}/pause</c>). The parameterless overload pauses for
    /// PayFast's default of 1 cycle.
    /// </summary>
    public Task<Subscription> PauseSubscriptionAsync(string subscriptionReference, int cycles, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        if (cycles < 1)
            throw new BhenguPaymentException(ProviderName, "Pause cycles must be at least 1.", "invalid_pause_cycles");
        return RunOperationAsync("pause_subscription", () => PauseSubscriptionCoreAsync(subscriptionReference, cycles, ct), ct);
    }

    private async Task<Subscription> PauseSubscriptionCoreAsync(string subscriptionReference, int? cycles, CancellationToken ct)
    {
        var body = new Dictionary<string, string>(StringComparer.Ordinal);
        if (cycles is { } c)
            body["cycles"] = c.ToString(CultureInfo.InvariantCulture);

        await SendSignedAsync<object>(
            HttpMethod.Put, $"subscriptions/{Uri.EscapeDataString(subscriptionReference)}/pause", body, ct)
            .ConfigureAwait(false);
        Logger.LogInformation("PayFast subscription paused: {Reference} cycles={Cycles}", subscriptionReference, cycles);
        var existing = await SafeGetAsync(subscriptionReference, ct).ConfigureAwait(false);
        return existing with { Status = SubscriptionStatus.Paused };
    }

    /// <inheritdoc/>
    public Task<Subscription> ResumeSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        return RunOperationAsync("resume_subscription", () => ResumeSubscriptionCoreAsync(subscriptionReference, ct), ct);
    }

    private async Task<Subscription> ResumeSubscriptionCoreAsync(string subscriptionReference, CancellationToken ct)
    {
        await SendSignedAsync<object>(
            HttpMethod.Put, $"subscriptions/{Uri.EscapeDataString(subscriptionReference)}/unpause", new Dictionary<string, string>(), ct)
            .ConfigureAwait(false);
        Logger.LogInformation("PayFast subscription resumed: {Reference}", subscriptionReference);
        var existing = await SafeGetAsync(subscriptionReference, ct).ConfigureAwait(false);
        return existing with { Status = SubscriptionStatus.Active };
    }

    /// <inheritdoc/>
    public Task<Subscription> UpdateSubscriptionAsync(string subscriptionReference, SubscriptionUpdateRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        ArgumentNullException.ThrowIfNull(request);
        return RunOperationAsync("update_subscription", () => UpdateSubscriptionCoreAsync(subscriptionReference, request, ct), ct);
    }

    private async Task<Subscription> UpdateSubscriptionCoreAsync(string subscriptionReference, SubscriptionUpdateRequest request, CancellationToken ct)
    {
        // PayFast: PATCH subscriptions/{token}/update — cycles / frequency / run_date / amount (cents).
        var body = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request.Amount is { } amount)
            body["amount"] = ((long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
        if (request.Interval is { } interval)
            body["frequency"] = MapFrequency(interval).ToString(CultureInfo.InvariantCulture);
        if (request.NextBillingDate is { } runDate)
            body["run_date"] = runDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (request.RemainingCycles is { } cycles)
            body["cycles"] = cycles.ToString(CultureInfo.InvariantCulture);

        if (body.Count == 0)
            throw new BhenguPaymentException(ProviderName, "SubscriptionUpdateRequest specified no fields to update.", "no_update_fields");

        await SendSignedAsync<object>(
            HttpMethod.Patch, $"subscriptions/{Uri.EscapeDataString(subscriptionReference)}/update", body, ct)
            .ConfigureAwait(false);
        Logger.LogInformation("PayFast subscription updated: {Reference} fields=[{Fields}]", subscriptionReference, string.Join(",", body.Keys));

        return await SafeGetAsync(subscriptionReference, ct).ConfigureAwait(false);
    }

    private async Task<Subscription> SafeGetAsync(string subscriptionReference, CancellationToken ct)
    {
        try
        {
            var existing = await GetSubscriptionCoreAsync(subscriptionReference, ct).ConfigureAwait(false);
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

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, body);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("PayFast {Method} {Path} failed: {Status} {Body}", method, relativePath, response.StatusCode, body);
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
            Logger.LogWarning(ex, "PayFast {Method} {Path} returned non-JSON body — accepting as ack", method, relativePath);
            return null;
        }
    }

    private string GetRedirectBaseUrl() => _options.UseSandbox
        ? (_options.SandboxUrl ?? "https://sandbox.payfast.co.za")
        : (_options.BaseUrl ?? "https://www.payfast.co.za");

    private static int MapFrequency(SubscriptionInterval interval) => interval switch
    {
        SubscriptionInterval.Daily => 1,
        SubscriptionInterval.Weekly => 2,
        SubscriptionInterval.Monthly => 3,
        SubscriptionInterval.Quarterly => 4,
        SubscriptionInterval.BiAnnually => 5,
        SubscriptionInterval.Annually => 6,
        // PayFast has no fortnightly/bi-weekly cadence — fail loudly rather than silently bill at the wrong interval.
        SubscriptionInterval.BiWeekly => throw new BhenguPaymentException(ProviderNames.PayFast,
            "PayFast does not support bi-weekly (fortnightly) billing. Use Weekly or Monthly.", "frequency_unsupported"),
        _ => throw new BhenguPaymentException(ProviderNames.PayFast,
            $"Unsupported PayFast subscription interval '{interval}'.", "frequency_unsupported")
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
