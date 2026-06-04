// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Bhengu.Finance.Payments.Flutterwave.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Flutterwave.Providers;

/// <summary>
/// Flutterwave subscription provider — wraps <c>/v3/payment-plans</c> and the linked
/// <c>/v3/subscriptions</c> endpoints.
/// <para>
/// In Flutterwave's model a "payment plan" is the template (price + cadence) and a "subscription"
/// is the customer<->plan binding, typically created implicitly when a charge is initiated with the
/// <c>payment_plan</c> parameter set. <see cref="CreateSubscriptionAsync"/> therefore issues a
/// hosted charge that binds the customer to the plan.
/// </para>
/// <para>
/// Pause / resume are <b>not natively supported</b> by Flutterwave — calls throw
/// <see cref="BhenguPaymentException"/>. The contract documents this caveat.
/// </para>
/// </summary>
public sealed class FlutterwaveSubscriptionProvider : BhenguProviderBase, ISubscriptionProvider
{
    private readonly HttpClient _httpClient;
    private readonly FlutterwaveOptions _options;
    private readonly FlutterwaveIdempotencyCache _idempotencyCache;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Flutterwave;

    /// <summary>Construct the provider; configures Bearer auth on the injected <paramref name="httpClient"/>.</summary>
    public FlutterwaveSubscriptionProvider(
        HttpClient httpClient,
        IOptions<FlutterwaveOptions> options,
        ILogger<FlutterwaveSubscriptionProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotencyCache = new FlutterwaveIdempotencyCache();

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(FlutterwaveOptions.SecretKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.flutterwave.com/");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
    }

    /// <inheritdoc/>
    public Task<Plan> CreatePlanAsync(PlanRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotencyCache.GetOrAddAsync(request.IdempotencyKey, () => CreatePlanCoreAsync(request, ct));
    }

    private async Task<Plan> CreatePlanCoreAsync(PlanRequest request, CancellationToken ct)
    {
        var (interval, duration) = MapIntervalToFlutterwave(request.Interval, request.TotalCycles);
        var body = new
        {
            amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
            name = request.Name,
            interval,
            duration,
            currency = request.Currency.ToUpperInvariant()
        };

        var responseBody = await SendAsync(HttpMethod.Post, "v3/payment-plans", body, ct, "CreatePlan").ConfigureAwait(false);
        var fw = JsonSerializer.Deserialize<FlutterwavePlanResponse>(responseBody);
        if (fw?.Data is null)
            throw new BhenguPaymentException(ProviderName, "Flutterwave CreatePlan returned no data");

        Logger.LogInformation("Flutterwave payment plan created: {Id} name={Name}", fw.Data.Id, fw.Data.Name);

        return ToPlan(fw.Data, request.Interval);
    }

    /// <inheritdoc/>
    public async Task<Plan?> GetPlanAsync(string planReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(planReference);
        try
        {
            var responseBody = await SendAsync(HttpMethod.Get, $"v3/payment-plans/{Uri.EscapeDataString(planReference)}", body: null, ct, "GetPlan").ConfigureAwait(false);
            var fw = JsonSerializer.Deserialize<FlutterwavePlanResponse>(responseBody);
            return fw?.Data is null ? null : ToPlan(fw.Data, MapIntervalFromFlutterwave(fw.Data.Interval));
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Flutterwave creates a subscription implicitly when a hosted charge includes the
    /// <c>payment_plan</c> parameter — there is no standalone <c>POST /subscriptions</c>. This method
    /// initialises the hosted charge and returns a <see cref="Subscription"/> bound to the resulting
    /// <c>tx_ref</c> (which doubles as the subscription correlator until the first charge settles).
    /// </remarks>
    public Task<Subscription> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotencyCache.GetOrAddAsync(request.IdempotencyKey, () => CreateSubscriptionCoreAsync(request, ct));
    }

    private async Task<Subscription> CreateSubscriptionCoreAsync(SubscriptionRequest request, CancellationToken ct)
    {
        var txRef = $"sub-{Guid.NewGuid():N}";
        var body = new
        {
            tx_ref = txRef,
            amount = "0",
            currency = "USD",
            payment_plan = request.PlanReference,
            redirect_url = _options.RedirectUrl,
            customer = new { email = request.CustomerId },
            customizations = new { title = $"Subscribe to {request.PlanReference}" }
        };

        await SendAsync(HttpMethod.Post, "v3/payments", body, ct, "CreateSubscription").ConfigureAwait(false);

        Logger.LogInformation("Flutterwave subscription initialised: {Reference} plan={Plan} customer={Customer}",
            txRef, request.PlanReference, request.CustomerId);

        return new Subscription
        {
            Reference = txRef,
            PlanReference = request.PlanReference,
            CustomerId = request.CustomerId,
            Status = SubscriptionStatus.Active,
            StartedAt = request.StartAt ?? DateTime.UtcNow,
            CyclesCompleted = 0
        };
    }

    /// <inheritdoc/>
    public async Task<Subscription?> GetSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        try
        {
            var responseBody = await SendAsync(HttpMethod.Get, $"v3/subscriptions/{Uri.EscapeDataString(subscriptionReference)}", body: null, ct, "GetSubscription").ConfigureAwait(false);
            var fw = JsonSerializer.Deserialize<FlutterwaveSubscriptionResponse>(responseBody);
            return fw?.Data is null ? null : ToSubscription(fw.Data);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Cancels the bound payment plan via <c>PUT /v3/payment-plans/:id/cancel</c> when
    /// <paramref name="immediately"/> is true; otherwise cancels the subscription instance via
    /// <c>PUT /v3/subscriptions/:id/cancel</c>. Idempotent — a second cancel on an
    /// already-cancelled subscription returns the same cancelled record without raising.
    /// </remarks>
    public async Task<Subscription> CancelSubscriptionAsync(string subscriptionReference, bool immediately = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);

        var path = immediately
            ? $"v3/payment-plans/{Uri.EscapeDataString(subscriptionReference)}/cancel"
            : $"v3/subscriptions/{Uri.EscapeDataString(subscriptionReference)}/cancel";

        try
        {
            var responseBody = await SendAsync(HttpMethod.Put, path, body: new { }, ct, "CancelSubscription").ConfigureAwait(false);
            var fw = JsonSerializer.Deserialize<FlutterwaveSubscriptionResponse>(responseBody);
            Logger.LogInformation("Flutterwave subscription {Reference} cancelled (immediately={Immediately})", subscriptionReference, immediately);

            return fw?.Data is null
                ? CancelledStub(subscriptionReference)
                : ToSubscription(fw.Data) with { Status = SubscriptionStatus.Cancelled, CancelledAt = DateTime.UtcNow };
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            // Idempotent semantics: not-found means already cancelled / never existed → return a stub.
            return CancelledStub(subscriptionReference);
        }
    }

    /// <inheritdoc/>
    /// <remarks>Flutterwave does not support pausing a subscription via API.</remarks>
    public Task<Subscription> PauseSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        throw new BhenguPaymentException(ProviderName,
            "Flutterwave does not expose a pause-subscription API. Cancel and recreate instead.");
    }

    /// <inheritdoc/>
    /// <remarks>Flutterwave does not support pausing a subscription, so resume is also unsupported.</remarks>
    public Task<Subscription> ResumeSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        throw new BhenguPaymentException(ProviderName,
            "Flutterwave does not expose a resume-subscription API.");
    }

    private static (string interval, int duration) MapIntervalToFlutterwave(SubscriptionInterval interval, int? totalCycles) => interval switch
    {
        SubscriptionInterval.Daily      => ("daily",      totalCycles ?? 0),
        SubscriptionInterval.Weekly     => ("weekly",     totalCycles ?? 0),
        SubscriptionInterval.BiWeekly   => ("weekly",     (totalCycles ?? 0) * 2), // Flutterwave has no bi-weekly; emulate via 2x weekly.
        SubscriptionInterval.Monthly    => ("monthly",    totalCycles ?? 0),
        SubscriptionInterval.Quarterly  => ("quarterly",  totalCycles ?? 0),
        SubscriptionInterval.BiAnnually => ("biannually", totalCycles ?? 0),
        SubscriptionInterval.Annually   => ("yearly",     totalCycles ?? 0),
        _                               => ("monthly",    totalCycles ?? 0)
    };

    private static SubscriptionInterval MapIntervalFromFlutterwave(string? raw) => raw?.ToLowerInvariant() switch
    {
        "daily"      => SubscriptionInterval.Daily,
        "weekly"     => SubscriptionInterval.Weekly,
        "monthly"    => SubscriptionInterval.Monthly,
        "quarterly"  => SubscriptionInterval.Quarterly,
        "biannually" => SubscriptionInterval.BiAnnually,
        "yearly"     => SubscriptionInterval.Annually,
        _            => SubscriptionInterval.Monthly
    };

    private static Plan ToPlan(FlutterwavePlanData data, SubscriptionInterval interval) => new()
    {
        Reference = data.Id.ToString(CultureInfo.InvariantCulture),
        Name = data.Name ?? string.Empty,
        Amount = data.Amount,
        Currency = data.Currency ?? string.Empty,
        Interval = interval,
        TotalCycles = data.Duration == 0 ? null : data.Duration
    };

    private static Subscription ToSubscription(FlutterwaveSubscriptionData data) => new()
    {
        Reference = data.Id.ToString(CultureInfo.InvariantCulture),
        PlanReference = data.PlanId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        CustomerId = data.CustomerEmail ?? data.CustomerId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        Status = MapSubscriptionStatus(data.Status),
        StartedAt = data.CreatedAt ?? DateTime.UtcNow,
        NextBillingAt = data.NextDue,
        CancelledAt = string.Equals(data.Status, "cancelled", StringComparison.OrdinalIgnoreCase) ? data.UpdatedAt : null,
        CyclesCompleted = 0
    };

    private static Subscription CancelledStub(string reference) => new()
    {
        Reference = reference,
        PlanReference = string.Empty,
        CustomerId = string.Empty,
        Status = SubscriptionStatus.Cancelled,
        StartedAt = DateTime.UtcNow,
        CancelledAt = DateTime.UtcNow,
        CyclesCompleted = 0
    };

    private static SubscriptionStatus MapSubscriptionStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "active"     => SubscriptionStatus.Active,
        "cancelled"  => SubscriptionStatus.Cancelled,
        "completed"  => SubscriptionStatus.Expired,
        "paused"     => SubscriptionStatus.Paused,
        "past_due"   => SubscriptionStatus.PastDue,
        "trialing"   => SubscriptionStatus.Trialing,
        _            => SubscriptionStatus.Active
    };

    private async Task<string> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string operation)
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Flutterwave failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Flutterwave {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    // === Flutterwave response shapes (internal) ===

    private sealed class FlutterwavePlanResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public FlutterwavePlanData? Data { get; set; }
    }

    private sealed class FlutterwavePlanData
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("interval")] public string? Interval { get; set; }
        [JsonPropertyName("duration")] public int Duration { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class FlutterwaveSubscriptionResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public FlutterwaveSubscriptionData? Data { get; set; }
    }

    private sealed class FlutterwaveSubscriptionData
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("plan")] public long? PlanId { get; set; }
        [JsonPropertyName("customer_id")] public long? CustomerId { get; set; }
        [JsonPropertyName("customer")] public string? CustomerEmail { get; set; }
        [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }
        [JsonPropertyName("updated_at")] public DateTime? UpdatedAt { get; set; }
        [JsonPropertyName("next_due")] public DateTime? NextDue { get; set; }
    }
}
