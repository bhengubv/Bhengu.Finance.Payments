// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.PayJustNow.Configuration;
using Bhengu.Finance.Payments.PayJustNow.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayJustNow.Providers;

/// <summary>
/// PayJustNow implementation of <see cref="ISubscriptionProvider"/>. Wraps PayJustNow's
/// instalment-plan endpoint — every BNPL order is itself a 3-instalment subscription, and the
/// merchant-facing surface exposes a <c>/plans</c> collection for higher-cycle agreements.
/// </summary>
public sealed class PayJustNowSubscriptionProvider : BhenguProviderBase, ISubscriptionProvider
{
    private readonly HttpClient _httpClient;
    private readonly PayJustNowOptions _options;
    private readonly PayJustNowIdempotencyCache _idempotency;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.PayJustNow;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public PayJustNowSubscriptionProvider(
        HttpClient httpClient,
        IOptions<PayJustNowOptions> options,
        ILogger<PayJustNowSubscriptionProvider> logger,
        PayJustNowIdempotencyCache idempotency)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayJustNowOptions.ApiKey)} is required");

        PayJustNowHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public Task<Plan> CreatePlanAsync(PlanRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, () => CreatePlanCoreAsync(request, ct), ct);
    }

    private async Task<Plan> CreatePlanCoreAsync(PlanRequest request, CancellationToken ct)
    {
        var body = new
        {
            name = request.Name,
            amount = (int)(request.Amount * 100m),
            currency = request.Currency.ToUpperInvariant(),
            interval = MapInterval(request.Interval),
            instalment_count = request.TotalCycles ?? 3,
            description = request.Description
        };

        var responseBody = await PayJustNowHttpClient.SendAsync(
            _httpClient, Logger, HttpMethod.Post, "plans", body, "CreatePlan", ct, request.IdempotencyKey).ConfigureAwait(false);
        var plan = JsonSerializer.Deserialize<PjnPlan>(responseBody, PayJustNowHttpClient.Json)
            ?? throw new BhenguPaymentException(ProviderName, "PayJustNow plan create returned no payload", "no_plan_data");

        return MapPlan(plan, request);
    }

    /// <inheritdoc/>
    public async Task<Plan?> GetPlanAsync(string planReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(planReference);
        try
        {
            var responseBody = await PayJustNowHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Get, $"plans/{Uri.EscapeDataString(planReference)}", null, "GetPlan", ct).ConfigureAwait(false);
            var plan = JsonSerializer.Deserialize<PjnPlan>(responseBody, PayJustNowHttpClient.Json);
            return plan is null ? null : MapPlan(plan, null);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public Task<Subscription> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, () => CreateSubscriptionCoreAsync(request, ct), ct);
    }

    private async Task<Subscription> CreateSubscriptionCoreAsync(SubscriptionRequest request, CancellationToken ct)
    {
        var body = new
        {
            plan_id = request.PlanReference,
            customer_token = request.PaymentMethodToken,
            shopper_reference = request.CustomerId,
            start_date = request.StartAt?.ToString("o", CultureInfo.InvariantCulture),
            trial_days = request.TrialDays
        };

        var responseBody = await PayJustNowHttpClient.SendAsync(
            _httpClient, Logger, HttpMethod.Post, "subscriptions", body, "CreateSubscription", ct, request.IdempotencyKey).ConfigureAwait(false);
        var sub = JsonSerializer.Deserialize<PjnSubscription>(responseBody, PayJustNowHttpClient.Json)
            ?? throw new BhenguPaymentException(ProviderName, "PayJustNow subscription create returned no payload", "no_subscription_data");
        return MapSubscription(sub, request);
    }

    /// <inheritdoc/>
    public async Task<Subscription?> GetSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        try
        {
            var responseBody = await PayJustNowHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Get, $"subscriptions/{Uri.EscapeDataString(subscriptionReference)}", null, "GetSubscription", ct).ConfigureAwait(false);
            var sub = JsonSerializer.Deserialize<PjnSubscription>(responseBody, PayJustNowHttpClient.Json);
            return sub is null ? null : MapSubscription(sub, null);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<Subscription> CancelSubscriptionAsync(string subscriptionReference, bool immediately = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        return await TransitionAsync(subscriptionReference, immediately ? "cancel" : "cancel_at_period_end", SubscriptionStatus.Cancelled, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<Subscription> PauseSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        return await TransitionAsync(subscriptionReference, "pause", SubscriptionStatus.Paused, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<Subscription> ResumeSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        return await TransitionAsync(subscriptionReference, "resume", SubscriptionStatus.Active, ct).ConfigureAwait(false);
    }

    private async Task<Subscription> TransitionAsync(string subscriptionReference, string action, SubscriptionStatus expected, CancellationToken ct)
    {
        try
        {
            var responseBody = await PayJustNowHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Post,
                $"subscriptions/{Uri.EscapeDataString(subscriptionReference)}/{action}",
                new { }, $"Subscription.{action}", ct).ConfigureAwait(false);
            var sub = JsonSerializer.Deserialize<PjnSubscription>(responseBody, PayJustNowHttpClient.Json);
            if (sub is not null) return MapSubscription(sub, null);
        }
        catch (PaymentDeclinedException ex) when (action == "cancel"
            && ex.ProviderErrorMessage?.Contains("already", StringComparison.OrdinalIgnoreCase) == true)
        {
            // idempotent cancel — already cancelled = success.
        }

        return new Subscription
        {
            Reference = subscriptionReference,
            PlanReference = string.Empty,
            CustomerId = string.Empty,
            Status = expected,
            StartedAt = DateTime.UtcNow,
            CancelledAt = expected == SubscriptionStatus.Cancelled ? DateTime.UtcNow : null,
            CyclesCompleted = 0
        };
    }

    private static string MapInterval(SubscriptionInterval interval) => interval switch
    {
        SubscriptionInterval.Daily => "daily",
        SubscriptionInterval.Weekly => "weekly",
        SubscriptionInterval.BiWeekly => "biweekly",
        SubscriptionInterval.Monthly => "monthly",
        SubscriptionInterval.Quarterly => "quarterly",
        SubscriptionInterval.BiAnnually => "biannually",
        SubscriptionInterval.Annually => "annually",
        _ => "monthly"
    };

    private static SubscriptionInterval MapIntervalFrom(string? raw) => raw?.ToLowerInvariant() switch
    {
        "daily" => SubscriptionInterval.Daily,
        "weekly" => SubscriptionInterval.Weekly,
        "biweekly" => SubscriptionInterval.BiWeekly,
        "monthly" => SubscriptionInterval.Monthly,
        "quarterly" => SubscriptionInterval.Quarterly,
        "biannually" => SubscriptionInterval.BiAnnually,
        "annually" => SubscriptionInterval.Annually,
        _ => SubscriptionInterval.Monthly
    };

    private static SubscriptionStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "active" => SubscriptionStatus.Active,
        "paused" or "suspended" => SubscriptionStatus.Paused,
        "trialing" or "trial" => SubscriptionStatus.Trialing,
        "past_due" => SubscriptionStatus.PastDue,
        "cancelled" or "canceled" => SubscriptionStatus.Cancelled,
        "expired" or "completed" => SubscriptionStatus.Expired,
        _ => SubscriptionStatus.Active
    };

    private static Plan MapPlan(PjnPlan plan, PlanRequest? fallback) => new()
    {
        Reference = plan.Id ?? fallback?.Name ?? string.Empty,
        Name = plan.Name ?? fallback?.Name ?? string.Empty,
        Amount = plan.Amount.HasValue ? plan.Amount.Value / 100m : fallback?.Amount ?? 0m,
        Currency = plan.Currency ?? fallback?.Currency ?? "ZAR",
        Interval = MapIntervalFrom(plan.Interval) is { } i ? i : fallback?.Interval ?? SubscriptionInterval.Monthly,
        TotalCycles = plan.InstalmentCount > 0 ? plan.InstalmentCount : fallback?.TotalCycles,
        Description = plan.Description ?? fallback?.Description
    };

    private static Subscription MapSubscription(PjnSubscription sub, SubscriptionRequest? fallback) => new()
    {
        Reference = sub.Id ?? string.Empty,
        PlanReference = sub.PlanId ?? fallback?.PlanReference ?? string.Empty,
        CustomerId = sub.ShopperReference ?? fallback?.CustomerId ?? string.Empty,
        Status = MapStatus(sub.Status),
        StartedAt = sub.StartedAt ?? DateTime.UtcNow,
        NextBillingAt = sub.NextBillingAt,
        CancelledAt = string.Equals(sub.Status, "cancelled", StringComparison.OrdinalIgnoreCase) ? DateTime.UtcNow : null,
        CyclesCompleted = sub.CompletedInstalments ?? 0
    };

    private sealed class PjnPlan
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("amount")] public int? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("interval")] public string? Interval { get; set; }
        [JsonPropertyName("instalment_count")] public int InstalmentCount { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }

    private sealed class PjnSubscription
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("plan_id")] public string? PlanId { get; set; }
        [JsonPropertyName("shopper_reference")] public string? ShopperReference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("started_at")] public DateTime? StartedAt { get; set; }
        [JsonPropertyName("next_billing_at")] public DateTime? NextBillingAt { get; set; }
        [JsonPropertyName("completed_instalments")] public int? CompletedInstalments { get; set; }
    }
}
