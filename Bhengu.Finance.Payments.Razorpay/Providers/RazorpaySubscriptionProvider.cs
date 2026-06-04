// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Razorpay.Providers;

/// <summary>
/// Razorpay recurring-billing provider. Wraps the <c>/v1/plans</c> and <c>/v1/subscriptions</c>
/// REST endpoints.
/// </summary>
/// <remarks>
/// Razorpay's data model: a Plan defines a price + period (daily/weekly/monthly/yearly) with an
/// interval multiplier. A Subscription binds a Plan to a customer for a fixed or open-ended number
/// of billing cycles. Razorpay only supports <c>cancel_at_cycle_end</c> via the <c>cancel_at_cycle_end</c>
/// query parameter on cancel — pass <c>immediately=false</c> to use it.
/// </remarks>
public sealed class RazorpaySubscriptionProvider : BhenguProviderBase, ISubscriptionProvider
{
    private readonly RazorpayHttpClient _http;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Razorpay;

    /// <summary>Create a new subscription provider bound to the supplied HTTP client and options.</summary>
    public RazorpaySubscriptionProvider(
        HttpClient httpClient,
        IOptions<RazorpayOptions> options,
        ILogger<RazorpaySubscriptionProvider> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _http = new RazorpayHttpClient(httpClient, options.Value, ProviderName, logger);
    }

    /// <inheritdoc />
    public async Task<Plan> CreatePlanAsync(PlanRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (period, interval) = MapInterval(request.Interval);
        var amountInPaise = (long)(request.Amount * 100);

        var body = new
        {
            period,
            interval,
            item = new
            {
                name = request.Name,
                amount = amountInPaise,
                currency = request.Currency.ToUpperInvariant(),
                description = request.Description
            },
            notes = request.Description is null ? null : new Dictionary<string, string> { ["description"] = request.Description }
        };

        var raw = await _http.SendAsync(HttpMethod.Post, "v1/plans", body, ct, "CreatePlan", request.IdempotencyKey).ConfigureAwait(false);
        var plan = RazorpayHttpClient.DeserialiseOrThrow<RazorpayPlan>(raw, ProviderName, "CreatePlan");

        Logger.LogInformation("Razorpay plan created: {PlanId} period={Period}x{Interval}", plan.Id, period, interval);

        return MapPlan(plan, request.TotalCycles, request.Description);
    }

    /// <inheritdoc />
    public async Task<Plan?> GetPlanAsync(string planReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planReference);

        try
        {
            var raw = await _http.GetAsync($"v1/plans/{Uri.EscapeDataString(planReference)}", ct, "GetPlan").ConfigureAwait(false);
            var p = RazorpayHttpClient.DeserialiseOrThrow<RazorpayPlan>(raw, ProviderName, "GetPlan");
            return MapPlan(p, totalCycles: null, description: p.Item?.Description);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404" || ex.ProviderErrorCode == "400")
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<Subscription> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new
        {
            plan_id = request.PlanReference,
            customer_id = request.CustomerId,
            total_count = 12,
            quantity = 1,
            start_at = request.StartAt is null ? (long?)null : new DateTimeOffset(DateTime.SpecifyKind(request.StartAt.Value, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            // Razorpay supports a token field for charging an already-vaulted method.
            token = string.IsNullOrWhiteSpace(request.PaymentMethodToken) ? null : request.PaymentMethodToken,
            // expire_by lets the customer authorise within a window
            expire_by = (long?)null,
            customer_notify = 1,
            // Razorpay uses "trial_period" in days (only on certain plans). Some flows use offer_id instead.
            notes = request.Metadata
        };

        var raw = await _http.SendAsync(HttpMethod.Post, "v1/subscriptions", body, ct, "CreateSubscription", request.IdempotencyKey).ConfigureAwait(false);
        var sub = RazorpayHttpClient.DeserialiseOrThrow<RazorpaySubscription>(raw, ProviderName, "CreateSubscription");

        Logger.LogInformation("Razorpay subscription created: {SubId} plan={PlanId} status={Status}",
            sub.Id, sub.PlanId, sub.Status);

        return MapSubscription(sub, request.CustomerId);
    }

    /// <inheritdoc />
    public async Task<Subscription?> GetSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionReference);

        try
        {
            var raw = await _http.GetAsync($"v1/subscriptions/{Uri.EscapeDataString(subscriptionReference)}", ct, "GetSubscription").ConfigureAwait(false);
            var s = RazorpayHttpClient.DeserialiseOrThrow<RazorpaySubscription>(raw, ProviderName, "GetSubscription");
            return MapSubscription(s, s.CustomerId ?? string.Empty);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404" || ex.ProviderErrorCode == "400")
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<Subscription> CancelSubscriptionAsync(string subscriptionReference, bool immediately = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionReference);

        var body = new
        {
            cancel_at_cycle_end = immediately ? 0 : 1
        };

        try
        {
            var raw = await _http.SendAsync(HttpMethod.Post, $"v1/subscriptions/{Uri.EscapeDataString(subscriptionReference)}/cancel", body, ct, "CancelSubscription").ConfigureAwait(false);
            var s = RazorpayHttpClient.DeserialiseOrThrow<RazorpaySubscription>(raw, ProviderName, "CancelSubscription");
            return MapSubscription(s, s.CustomerId ?? string.Empty);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "400")
        {
            // Razorpay returns 400 with an "already cancelled" error when re-cancelling — treat as idempotent.
            var existing = await GetSubscriptionAsync(subscriptionReference, ct).ConfigureAwait(false);
            if (existing is { Status: SubscriptionStatus.Cancelled or SubscriptionStatus.Expired })
                return existing;
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Subscription> PauseSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionReference);

        var body = new { pause_at = "now" };
        var raw = await _http.SendAsync(HttpMethod.Post, $"v1/subscriptions/{Uri.EscapeDataString(subscriptionReference)}/pause", body, ct, "PauseSubscription").ConfigureAwait(false);
        var s = RazorpayHttpClient.DeserialiseOrThrow<RazorpaySubscription>(raw, ProviderName, "PauseSubscription");
        return MapSubscription(s, s.CustomerId ?? string.Empty);
    }

    /// <inheritdoc />
    public async Task<Subscription> ResumeSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionReference);

        var body = new { resume_at = "now" };
        var raw = await _http.SendAsync(HttpMethod.Post, $"v1/subscriptions/{Uri.EscapeDataString(subscriptionReference)}/resume", body, ct, "ResumeSubscription").ConfigureAwait(false);
        var s = RazorpayHttpClient.DeserialiseOrThrow<RazorpaySubscription>(raw, ProviderName, "ResumeSubscription");
        return MapSubscription(s, s.CustomerId ?? string.Empty);
    }

    // Razorpay plans have a period (daily / weekly / monthly / yearly) and an interval (multiplier).
    // We map the SDK's normalised intervals to those — sub-monthly intervals like BiWeekly become weekly*2,
    // BiAnnually becomes monthly*6.
    private static (string period, int interval) MapInterval(SubscriptionInterval i) => i switch
    {
        SubscriptionInterval.Daily => ("daily", 1),
        SubscriptionInterval.Weekly => ("weekly", 1),
        SubscriptionInterval.BiWeekly => ("weekly", 2),
        SubscriptionInterval.Monthly => ("monthly", 1),
        SubscriptionInterval.Quarterly => ("monthly", 3),
        SubscriptionInterval.BiAnnually => ("monthly", 6),
        SubscriptionInterval.Annually => ("yearly", 1),
        _ => ("monthly", 1)
    };

    private static SubscriptionInterval ParseInterval(string? period, int? interval)
    {
        return (period?.ToLowerInvariant(), interval) switch
        {
            ("daily", _) => SubscriptionInterval.Daily,
            ("weekly", 2) => SubscriptionInterval.BiWeekly,
            ("weekly", _) => SubscriptionInterval.Weekly,
            ("monthly", 3) => SubscriptionInterval.Quarterly,
            ("monthly", 6) => SubscriptionInterval.BiAnnually,
            ("monthly", _) => SubscriptionInterval.Monthly,
            ("yearly", _) => SubscriptionInterval.Annually,
            _ => SubscriptionInterval.Monthly
        };
    }

    private static SubscriptionStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "active" or "authenticated" => SubscriptionStatus.Active,
        "created" or "pending" => SubscriptionStatus.Active, // brand-new subscription not yet charged
        "halted" or "paused" => SubscriptionStatus.Paused,
        "cancelled" or "canceled" => SubscriptionStatus.Cancelled,
        "completed" or "expired" => SubscriptionStatus.Expired,
        "trialing" => SubscriptionStatus.Trialing,
        _ => SubscriptionStatus.Active
    };

    private static Plan MapPlan(RazorpayPlan plan, int? totalCycles, string? description)
    {
        var amount = (plan.Item?.Amount ?? 0L) / 100m;
        return new Plan
        {
            Reference = plan.Id ?? string.Empty,
            Name = plan.Item?.Name ?? string.Empty,
            Amount = amount,
            Currency = plan.Item?.Currency ?? "INR",
            Interval = ParseInterval(plan.Period, plan.Interval),
            TotalCycles = totalCycles,
            Description = description ?? plan.Item?.Description
        };
    }

    private static Subscription MapSubscription(RazorpaySubscription s, string customerId)
    {
        return new Subscription
        {
            Reference = s.Id ?? string.Empty,
            PlanReference = s.PlanId ?? string.Empty,
            CustomerId = customerId,
            Status = MapStatus(s.Status),
            StartedAt = s.StartAt is > 0 ? DateTimeOffset.FromUnixTimeSeconds(s.StartAt.Value).UtcDateTime : DateTime.UtcNow,
            NextBillingAt = s.ChargeAt is > 0 ? DateTimeOffset.FromUnixTimeSeconds(s.ChargeAt.Value).UtcDateTime : null,
            CancelledAt = s.EndedAt is > 0 ? DateTimeOffset.FromUnixTimeSeconds(s.EndedAt.Value).UtcDateTime : null,
            CyclesCompleted = s.PaidCount
        };
    }

    // === Razorpay API response shapes (internal) ===

    private sealed class RazorpayPlan
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; }
        [JsonPropertyName("period")] public string? Period { get; set; }
        [JsonPropertyName("item")] public RazorpayPlanItem? Item { get; set; }
        [JsonPropertyName("created_at")] public long? CreatedAt { get; set; }
    }

    private sealed class RazorpayPlanItem
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }

    private sealed class RazorpaySubscription
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("plan_id")] public string? PlanId { get; set; }
        [JsonPropertyName("customer_id")] public string? CustomerId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("current_start")] public long? CurrentStart { get; set; }
        [JsonPropertyName("current_end")] public long? CurrentEnd { get; set; }
        [JsonPropertyName("ended_at")] public long? EndedAt { get; set; }
        [JsonPropertyName("quantity")] public int Quantity { get; set; }
        [JsonPropertyName("paid_count")] public int PaidCount { get; set; }
        [JsonPropertyName("total_count")] public int TotalCount { get; set; }
        [JsonPropertyName("start_at")] public long? StartAt { get; set; }
        [JsonPropertyName("charge_at")] public long? ChargeAt { get; set; }
    }
}
