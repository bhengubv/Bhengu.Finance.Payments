// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Paymob.Configuration;
using Bhengu.Finance.Payments.Paymob.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Paymob.Providers;

/// <summary>
/// Paymob implementation of <see cref="ISubscriptionProvider"/> backed by Paymob's Plans and
/// Subscriptions APIs (<c>/api/acceptance/plans</c>, <c>/api/acceptance/subscriptions</c>).
/// </summary>
/// <remarks>
/// Paymob exposes pause / resume via a single <c>state</c> field on the subscription; the
/// adapter normalises those into <see cref="SubscriptionStatus.Paused"/>/<see cref="SubscriptionStatus.Active"/>.
/// </remarks>
public sealed class PaymobSubscriptionProvider : BhenguProviderBase, ISubscriptionProvider
{
    private readonly HttpClient _httpClient;
    private readonly PaymobOptions _options;
    private readonly PaymobIdempotencyCache _idempotency;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Paymob;

    /// <summary>Construct a subscription provider. Designed to be registered via DI.</summary>
    public PaymobSubscriptionProvider(
        HttpClient httpClient,
        IOptions<PaymobOptions> options,
        ILogger<PaymobSubscriptionProvider> logger,
        PaymobIdempotencyCache idempotency)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaymobOptions.ApiKey)} is required");

        PaymobHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public Task<Plan> CreatePlanAsync(PlanRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, () => CreatePlanCoreAsync(request, ct), ct);
    }

    private async Task<Plan> CreatePlanCoreAsync(PlanRequest request, CancellationToken ct)
    {
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "createPlan");
        var authToken = await PaymobHttpClient.AuthenticateAsync(_httpClient, Logger, _options, ct).ConfigureAwait(false);

        var body = new
        {
            auth_token = authToken,
            name = request.Name,
            amount_cents = (long)(request.Amount * 100m),
            currency = request.Currency.ToUpperInvariant(),
            frequency_value = 1,
            frequency_unit = MapInterval(request.Interval),
            number_of_deductions = request.TotalCycles ?? 0,
            description = request.Description,
            integration = _options.IntegrationId
        };

        var responseBody = await PaymobHttpClient.SendAsync(
            _httpClient, Logger, HttpMethod.Post, "api/acceptance/plans", body, "CreatePlan", ct).ConfigureAwait(false);
        var plan = JsonSerializer.Deserialize<PaymobPlanResponse>(responseBody, PaymobHttpClient.Json)
            ?? throw new BhenguPaymentException(ProviderName, "Paymob plan create returned no payload", "no_plan_data");

        return MapPlan(plan, request);
    }

    /// <inheritdoc/>
    public async Task<Plan?> GetPlanAsync(string planReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(planReference);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "getPlan");
        try
        {
            var authToken = await PaymobHttpClient.AuthenticateAsync(_httpClient, Logger, _options, ct).ConfigureAwait(false);
            var responseBody = await PaymobHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Get,
                $"api/acceptance/plans/{Uri.EscapeDataString(planReference)}?auth_token={Uri.EscapeDataString(authToken)}",
                null, "GetPlan", ct).ConfigureAwait(false);

            var plan = JsonSerializer.Deserialize<PaymobPlanResponse>(responseBody, PaymobHttpClient.Json);
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
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "createSubscription");
        var authToken = await PaymobHttpClient.AuthenticateAsync(_httpClient, Logger, _options, ct).ConfigureAwait(false);

        var body = new
        {
            auth_token = authToken,
            plan_id = request.PlanReference,
            payment_token = request.PaymentMethodToken,
            identifier = request.CustomerId,
            start_date = request.StartAt?.ToString("o", CultureInfo.InvariantCulture),
            trial_period_days = request.TrialDays
        };

        var responseBody = await PaymobHttpClient.SendAsync(
            _httpClient, Logger, HttpMethod.Post, "api/acceptance/subscriptions", body, "CreateSubscription", ct).ConfigureAwait(false);
        var sub = JsonSerializer.Deserialize<PaymobSubscriptionResponse>(responseBody, PaymobHttpClient.Json)
            ?? throw new BhenguPaymentException(ProviderName, "Paymob subscription create returned no payload", "no_subscription_data");

        return MapSubscription(sub, request);
    }

    /// <inheritdoc/>
    public async Task<Subscription?> GetSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "getSubscription");
        try
        {
            var authToken = await PaymobHttpClient.AuthenticateAsync(_httpClient, Logger, _options, ct).ConfigureAwait(false);
            var responseBody = await PaymobHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Get,
                $"api/acceptance/subscriptions/{Uri.EscapeDataString(subscriptionReference)}?auth_token={Uri.EscapeDataString(authToken)}",
                null, "GetSubscription", ct).ConfigureAwait(false);

            var sub = JsonSerializer.Deserialize<PaymobSubscriptionResponse>(responseBody, PaymobHttpClient.Json);
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
        return await TransitionAsync(subscriptionReference, immediately ? "cancel_immediate" : "cancel", SubscriptionStatus.Cancelled, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<Subscription> PauseSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        return await TransitionAsync(subscriptionReference, "suspend", SubscriptionStatus.Paused, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<Subscription> ResumeSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        return await TransitionAsync(subscriptionReference, "resume", SubscriptionStatus.Active, ct).ConfigureAwait(false);
    }

    private async Task<Subscription> TransitionAsync(string subscriptionReference, string action, SubscriptionStatus expected, CancellationToken ct)
    {
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, $"subscription.{action}");
        var authToken = await PaymobHttpClient.AuthenticateAsync(_httpClient, Logger, _options, ct).ConfigureAwait(false);
        var body = new { auth_token = authToken, action };

        try
        {
            var responseBody = await PaymobHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Post,
                $"api/acceptance/subscriptions/{Uri.EscapeDataString(subscriptionReference)}/action",
                body, $"Subscription.{action}", ct).ConfigureAwait(false);
            var sub = JsonSerializer.Deserialize<PaymobSubscriptionResponse>(responseBody, PaymobHttpClient.Json);
            if (sub is not null) return MapSubscription(sub, null);
        }
        catch (PaymentDeclinedException ex) when (action.StartsWith("cancel", StringComparison.Ordinal)
            && ex.ProviderErrorMessage?.Contains("already", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Idempotent — cancelling an already-cancelled subscription is success.
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
        SubscriptionInterval.Daily => "day",
        SubscriptionInterval.Weekly => "week",
        SubscriptionInterval.BiWeekly => "week",
        SubscriptionInterval.Monthly => "month",
        SubscriptionInterval.Quarterly => "month",
        SubscriptionInterval.BiAnnually => "month",
        SubscriptionInterval.Annually => "year",
        _ => "month"
    };

    private static SubscriptionInterval MapIntervalFromPaymob(string? raw, int? frequencyValue) => raw?.ToLowerInvariant() switch
    {
        "day" => SubscriptionInterval.Daily,
        "week" => frequencyValue == 2 ? SubscriptionInterval.BiWeekly : SubscriptionInterval.Weekly,
        "month" => frequencyValue switch
        {
            3 => SubscriptionInterval.Quarterly,
            6 => SubscriptionInterval.BiAnnually,
            _ => SubscriptionInterval.Monthly
        },
        "year" => SubscriptionInterval.Annually,
        _ => SubscriptionInterval.Monthly
    };

    private static SubscriptionStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "active" or "running" => SubscriptionStatus.Active,
        "suspended" or "paused" => SubscriptionStatus.Paused,
        "trialing" or "trial" => SubscriptionStatus.Trialing,
        "past_due" or "delinquent" => SubscriptionStatus.PastDue,
        "cancelled" or "canceled" => SubscriptionStatus.Cancelled,
        "expired" or "completed" => SubscriptionStatus.Expired,
        _ => SubscriptionStatus.Active
    };

    private static Plan MapPlan(PaymobPlanResponse plan, PlanRequest? fallback) => new()
    {
        Reference = plan.Id?.ToString(CultureInfo.InvariantCulture) ?? fallback?.Name ?? string.Empty,
        Name = plan.Name ?? fallback?.Name ?? string.Empty,
        Amount = plan.AmountCents / 100m,
        Currency = plan.Currency ?? fallback?.Currency ?? "EGP",
        Interval = MapIntervalFromPaymob(plan.FrequencyUnit, plan.FrequencyValue),
        TotalCycles = plan.NumberOfDeductions > 0 ? plan.NumberOfDeductions : fallback?.TotalCycles,
        Description = plan.Description ?? fallback?.Description
    };

    private static Subscription MapSubscription(PaymobSubscriptionResponse sub, SubscriptionRequest? fallback) => new()
    {
        Reference = sub.Id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        PlanReference = sub.PlanId?.ToString(CultureInfo.InvariantCulture) ?? fallback?.PlanReference ?? string.Empty,
        CustomerId = sub.Identifier ?? fallback?.CustomerId ?? string.Empty,
        Status = MapStatus(sub.State),
        StartedAt = sub.StartDate ?? DateTime.UtcNow,
        NextBillingAt = sub.NextBillingDate,
        CancelledAt = string.Equals(sub.State, "cancelled", StringComparison.OrdinalIgnoreCase) ? DateTime.UtcNow : null,
        CyclesCompleted = sub.SuccessfulDeductions ?? 0
    };

    // === Paymob API shapes (internal) ===

    private sealed class PaymobPlanResponse
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("amount_cents")] public long AmountCents { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("frequency_value")] public int FrequencyValue { get; set; }
        [JsonPropertyName("frequency_unit")] public string? FrequencyUnit { get; set; }
        [JsonPropertyName("number_of_deductions")] public int NumberOfDeductions { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }

    private sealed class PaymobSubscriptionResponse
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("plan_id")] public long? PlanId { get; set; }
        [JsonPropertyName("identifier")] public string? Identifier { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("start_date")] public DateTime? StartDate { get; set; }
        [JsonPropertyName("next_billing_date")] public DateTime? NextBillingDate { get; set; }
        [JsonPropertyName("successful_deductions")] public int? SuccessfulDeductions { get; set; }
    }
}
