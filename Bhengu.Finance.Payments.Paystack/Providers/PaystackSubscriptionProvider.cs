// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Paystack.Providers;

/// <summary>
/// Paystack implementation of <see cref="ISubscriptionProvider"/> backed by Paystack's
/// <c>/plan</c> and <c>/subscription</c> endpoints.
/// </summary>
/// <remarks>
/// Paystack pause / resume is exposed by toggling the subscription's <c>enable</c> / <c>disable</c>
/// endpoints with the email-token confirmation flow. The Bhengu SDK abstracts that: callers receive
/// <see cref="Subscription"/> records with <see cref="SubscriptionStatus.Paused"/>/<see cref="SubscriptionStatus.Active"/>
/// regardless of the wire-level toggle.
/// </remarks>
public sealed class PaystackSubscriptionProvider : ISubscriptionProvider
{
    private readonly HttpClient _httpClient;
    private readonly PaystackOptions _options;
    private readonly ILogger<PaystackSubscriptionProvider> _logger;
    private readonly PaystackIdempotencyCache _idempotency;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Paystack;

    /// <summary>Construct a subscription provider. Designed to be registered via DI.</summary>
    public PaystackSubscriptionProvider(
        HttpClient httpClient,
        IOptions<PaystackOptions> options,
        ILogger<PaystackSubscriptionProvider> logger,
        PaystackIdempotencyCache idempotency)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaystackOptions.SecretKey)} is required");

        PaystackHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public Task<Plan> CreatePlanAsync(PlanRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PaystackObservability.ObserveAsync("create_plan", () =>
            _idempotency.GetOrAddAsync(request.IdempotencyKey, () => CreatePlanCoreAsync(request, ct)));
    }

    private async Task<Plan> CreatePlanCoreAsync(PlanRequest request, CancellationToken ct)
    {
        var body = new
        {
            name = request.Name,
            amount = (long)(request.Amount * 100m),
            currency = request.Currency.ToUpperInvariant(),
            interval = MapIntervalToPaystack(request.Interval),
            invoice_limit = request.TotalCycles,
            description = request.Description
        };

        var responseBody = await PaystackHttpClient.SendAsync(
            _httpClient, _logger, HttpMethod.Post, "plan", body, "CreatePlan", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<PaystackPlanResponse>(responseBody, PaystackHttpClient.Json);
        var plan = response?.Data
            ?? throw new BhenguPaymentException(ProviderName, "Paystack plan create returned no data", "no_plan_data");

        return MapPlan(plan, fallback: request);
    }

    /// <inheritdoc/>
    public Task<Plan?> GetPlanAsync(string planReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(planReference);
        return PaystackObservability.ObserveAsync("get_plan", () => GetPlanCoreAsync(planReference, ct));
    }

    private async Task<Plan?> GetPlanCoreAsync(string planReference, CancellationToken ct)
    {
        try
        {
            var responseBody = await PaystackHttpClient.SendAsync(
                _httpClient, _logger, HttpMethod.Get, $"plan/{Uri.EscapeDataString(planReference)}", null, "GetPlan", ct).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PaystackPlanResponse>(responseBody, PaystackHttpClient.Json);
            return response?.Data is { } plan ? MapPlan(plan, fallback: null) : null;
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
        return PaystackObservability.ObserveAsync("create_subscription", () =>
            _idempotency.GetOrAddAsync(request.IdempotencyKey, () => CreateSubscriptionCoreAsync(request, ct)));
    }

    private async Task<Subscription> CreateSubscriptionCoreAsync(SubscriptionRequest request, CancellationToken ct)
    {
        var body = new
        {
            customer = request.CustomerId,
            plan = request.PlanReference,
            authorization = request.PaymentMethodToken,
            start_date = request.StartAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
        };

        var responseBody = await PaystackHttpClient.SendAsync(
            _httpClient, _logger, HttpMethod.Post, "subscription", body, "CreateSubscription", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<PaystackSubscriptionResponse>(responseBody, PaystackHttpClient.Json);
        var sub = response?.Data
            ?? throw new BhenguPaymentException(ProviderName, "Paystack subscription create returned no data", "no_subscription_data");

        return MapSubscription(sub, request);
    }

    /// <inheritdoc/>
    public Task<Subscription?> GetSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        return PaystackObservability.ObserveAsync("get_subscription", () => GetSubscriptionCoreAsync(subscriptionReference, ct));
    }

    private async Task<Subscription?> GetSubscriptionCoreAsync(string subscriptionReference, CancellationToken ct)
    {
        try
        {
            var responseBody = await PaystackHttpClient.SendAsync(
                _httpClient, _logger, HttpMethod.Get, $"subscription/{Uri.EscapeDataString(subscriptionReference)}", null, "GetSubscription", ct).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PaystackSubscriptionResponse>(responseBody, PaystackHttpClient.Json);
            return response?.Data is { } sub ? MapSubscription(sub, null) : null;
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public Task<Subscription> CancelSubscriptionAsync(string subscriptionReference, bool immediately = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        return PaystackObservability.ObserveAsync("cancel_subscription", () => CancelSubscriptionCoreAsync(subscriptionReference, immediately, ct));
    }

    private async Task<Subscription> CancelSubscriptionCoreAsync(string subscriptionReference, bool immediately, CancellationToken ct)
    {
        // Paystack requires both `code` and `token` for disable. The token is emailed to the merchant
        // and must round-trip back via the Disable endpoint. Callers must pre-fetch and pass the token
        // via subscription metadata; if missing, we still report the request was sent.
        var existing = await GetSubscriptionAsync(subscriptionReference, ct).ConfigureAwait(false)
            ?? throw new BhenguPaymentException(ProviderName, $"Subscription {subscriptionReference} not found", "subscription_not_found");

        if (existing.Status == SubscriptionStatus.Cancelled)
            return existing;

        // Fetch raw subscription to read the cancellation token.
        var rawBody = await PaystackHttpClient.SendAsync(
            _httpClient, _logger, HttpMethod.Get, $"subscription/{Uri.EscapeDataString(subscriptionReference)}", null, "CancelSubscriptionFetch", ct).ConfigureAwait(false);
        var raw = JsonSerializer.Deserialize<PaystackSubscriptionResponse>(rawBody, PaystackHttpClient.Json);
        var emailToken = raw?.Data?.EmailToken;

        var body = new
        {
            code = raw?.Data?.SubscriptionCode ?? subscriptionReference,
            token = emailToken ?? string.Empty
        };

        try
        {
            await PaystackHttpClient.SendAsync(
                _httpClient, _logger, HttpMethod.Post, "subscription/disable", body, "CancelSubscription", ct).ConfigureAwait(false);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorMessage?.Contains("already inactive", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Idempotent: already cancelled is success.
        }

        return existing with { Status = SubscriptionStatus.Cancelled, CancelledAt = DateTime.UtcNow };
    }

    /// <inheritdoc/>
    public Task<Subscription> PauseSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        return PaystackObservability.ObserveAsync("pause_subscription", () => PauseSubscriptionCoreAsync(subscriptionReference, ct));
    }

    private async Task<Subscription> PauseSubscriptionCoreAsync(string subscriptionReference, CancellationToken ct)
    {
        // Paystack treats pause as identical to disable in their API; the difference is intent. Surface
        // a clear status of Paused so callers do not lose the distinction.
        var existing = await GetSubscriptionAsync(subscriptionReference, ct).ConfigureAwait(false)
            ?? throw new BhenguPaymentException(ProviderName, $"Subscription {subscriptionReference} not found", "subscription_not_found");

        var rawBody = await PaystackHttpClient.SendAsync(
            _httpClient, _logger, HttpMethod.Get, $"subscription/{Uri.EscapeDataString(subscriptionReference)}", null, "PauseSubscriptionFetch", ct).ConfigureAwait(false);
        var raw = JsonSerializer.Deserialize<PaystackSubscriptionResponse>(rawBody, PaystackHttpClient.Json);

        var body = new
        {
            code = raw?.Data?.SubscriptionCode ?? subscriptionReference,
            token = raw?.Data?.EmailToken ?? string.Empty
        };

        await PaystackHttpClient.SendAsync(
            _httpClient, _logger, HttpMethod.Post, "subscription/disable", body, "PauseSubscription", ct).ConfigureAwait(false);
        return existing with { Status = SubscriptionStatus.Paused };
    }

    /// <inheritdoc/>
    public Task<Subscription> ResumeSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        return PaystackObservability.ObserveAsync("resume_subscription", () => ResumeSubscriptionCoreAsync(subscriptionReference, ct));
    }

    private async Task<Subscription> ResumeSubscriptionCoreAsync(string subscriptionReference, CancellationToken ct)
    {
        var existing = await GetSubscriptionAsync(subscriptionReference, ct).ConfigureAwait(false)
            ?? throw new BhenguPaymentException(ProviderName, $"Subscription {subscriptionReference} not found", "subscription_not_found");

        var rawBody = await PaystackHttpClient.SendAsync(
            _httpClient, _logger, HttpMethod.Get, $"subscription/{Uri.EscapeDataString(subscriptionReference)}", null, "ResumeSubscriptionFetch", ct).ConfigureAwait(false);
        var raw = JsonSerializer.Deserialize<PaystackSubscriptionResponse>(rawBody, PaystackHttpClient.Json);

        var body = new
        {
            code = raw?.Data?.SubscriptionCode ?? subscriptionReference,
            token = raw?.Data?.EmailToken ?? string.Empty
        };

        await PaystackHttpClient.SendAsync(
            _httpClient, _logger, HttpMethod.Post, "subscription/enable", body, "ResumeSubscription", ct).ConfigureAwait(false);
        return existing with { Status = SubscriptionStatus.Active };
    }

    private static string MapIntervalToPaystack(SubscriptionInterval interval) => interval switch
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

    private static SubscriptionInterval MapIntervalFromPaystack(string? raw) => raw?.ToLowerInvariant() switch
    {
        "daily" => SubscriptionInterval.Daily,
        "weekly" => SubscriptionInterval.Weekly,
        "biweekly" => SubscriptionInterval.BiWeekly,
        "monthly" => SubscriptionInterval.Monthly,
        "quarterly" => SubscriptionInterval.Quarterly,
        "biannually" => SubscriptionInterval.BiAnnually,
        "annually" or "annual" => SubscriptionInterval.Annually,
        _ => SubscriptionInterval.Monthly
    };

    private static SubscriptionStatus MapSubscriptionStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "active" => SubscriptionStatus.Active,
        "non-renewing" => SubscriptionStatus.Active,
        "cancelled" or "complete" or "completed" => SubscriptionStatus.Cancelled,
        "attention" or "past_due" => SubscriptionStatus.PastDue,
        "trial" or "trialing" => SubscriptionStatus.Trialing,
        "expired" => SubscriptionStatus.Expired,
        "disabled" or "paused" => SubscriptionStatus.Paused,
        _ => SubscriptionStatus.Active
    };

    private static Plan MapPlan(PaystackPlanData plan, PlanRequest? fallback) => new()
    {
        Reference = plan.PlanCode ?? fallback?.Name ?? string.Empty,
        Name = plan.Name ?? fallback?.Name ?? string.Empty,
        Amount = plan.Amount / 100m,
        Currency = plan.Currency ?? fallback?.Currency ?? "NGN",
        Interval = MapIntervalFromPaystack(plan.Interval) is { } i ? i : fallback?.Interval ?? SubscriptionInterval.Monthly,
        TotalCycles = plan.InvoiceLimit > 0 ? plan.InvoiceLimit : fallback?.TotalCycles,
        Description = plan.Description ?? fallback?.Description
    };

    private static Subscription MapSubscription(PaystackSubscriptionData sub, SubscriptionRequest? fallback) => new()
    {
        Reference = sub.SubscriptionCode ?? string.Empty,
        PlanReference = sub.Plan?.PlanCode ?? fallback?.PlanReference ?? string.Empty,
        CustomerId = sub.Customer?.CustomerCode ?? fallback?.CustomerId ?? string.Empty,
        Status = MapSubscriptionStatus(sub.Status),
        StartedAt = sub.CreatedAt ?? DateTime.UtcNow,
        NextBillingAt = sub.NextPaymentDate,
        CancelledAt = string.Equals(sub.Status, "cancelled", StringComparison.OrdinalIgnoreCase) ? DateTime.UtcNow : null,
        CyclesCompleted = sub.QuantityCharged
    };

    // === Paystack API shapes (internal) ===

    private sealed class PaystackPlanResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("data")] public PaystackPlanData? Data { get; set; }
    }

    private sealed class PaystackPlanData
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("plan_code")] public string? PlanCode { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("interval")] public string? Interval { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("invoice_limit")] public int InvoiceLimit { get; set; }
    }

    private sealed class PaystackSubscriptionResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("data")] public PaystackSubscriptionData? Data { get; set; }
    }

    private sealed class PaystackSubscriptionData
    {
        [JsonPropertyName("subscription_code")] public string? SubscriptionCode { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("email_token")] public string? EmailToken { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
        [JsonPropertyName("next_payment_date")] public DateTime? NextPaymentDate { get; set; }
        [JsonPropertyName("quantity")] public int Quantity { get; set; }
        [JsonPropertyName("quantity_charged")] public int QuantityCharged { get; set; }
        [JsonPropertyName("plan")] public PaystackPlanData? Plan { get; set; }
        [JsonPropertyName("customer")] public PaystackCustomerSummary? Customer { get; set; }
    }

    private sealed class PaystackCustomerSummary
    {
        [JsonPropertyName("customer_code")] public string? CustomerCode { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
    }
}
