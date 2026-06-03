// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using Plan = Bhengu.Finance.Payments.Core.Models.Subscription.Plan;
using StripeSubscription = Stripe.Subscription;
using Subscription = Bhengu.Finance.Payments.Core.Models.Subscription.Subscription;

namespace Bhengu.Finance.Payments.Stripe.Providers;

/// <summary>
/// Stripe implementation of <see cref="ISubscriptionProvider"/>. Wraps the Stripe Billing
/// stack — <c>Product</c>, <c>Price</c>, and <c>Subscription</c> objects — behind the unified
/// Bhengu plan / subscription contract.
/// </summary>
public sealed class StripeSubscriptionProvider : ISubscriptionProvider
{
    private readonly StripeOptions _options;
    private readonly ILogger<StripeSubscriptionProvider> _logger;
    private readonly IStripeClient _stripeClient;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Stripe;

    /// <summary>Construct the provider. Throws <see cref="ProviderConfigurationException"/> if <see cref="StripeOptions.SecretKey"/> is unset.</summary>
    public StripeSubscriptionProvider(
        HttpClient httpClient,
        IOptions<StripeOptions> options,
        ILogger<StripeSubscriptionProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(StripeOptions.SecretKey)} is required");

        StripeConfiguration.ApiKey = _options.SecretKey;
        _stripeClient = new StripeClient(
            apiKey: _options.SecretKey,
            httpClient: new SystemNetHttpClient(httpClient));
    }

    /// <inheritdoc />
    public async Task<Plan> CreatePlanAsync(PlanRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestOptions = BuildRequestOptions(request.IdempotencyKey);

        try
        {
            // Stripe's modern API uses Product + Price; the older Plan resource is still supported
            // and is the most natural one-to-one mapping for the Bhengu Plan contract.
            var planService = new PlanService(_stripeClient);
            var planOptions = new PlanCreateOptions
            {
                Amount = (long)(request.Amount * 100),
                Currency = request.Currency.ToLowerInvariant(),
                Interval = MapInterval(request.Interval),
                IntervalCount = IntervalCount(request.Interval),
                Nickname = request.Name,
                Product = new PlanProductOptions
                {
                    Name = request.Name,
                    StatementDescriptor = TruncateStatementDescriptor(request.Name)
                }
            };

            var plan = await planService.CreateAsync(planOptions, requestOptions, ct).ConfigureAwait(false);
            _logger.LogInformation("Stripe Plan created: {PlanId} amount={Amount} {Currency}", plan.Id, request.Amount, request.Currency);
            return Map(plan, request);
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "CreatePlan");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    /// <inheritdoc />
    public async Task<Plan?> GetPlanAsync(string planReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(planReference);

        try
        {
            var service = new PlanService(_stripeClient);
            var plan = await service.GetAsync(planReference, cancellationToken: ct).ConfigureAwait(false);
            return Map(plan);
        }
        catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "GetPlan");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    /// <inheritdoc />
    public async Task<Subscription> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestOptions = BuildRequestOptions(request.IdempotencyKey);

        try
        {
            var service = new SubscriptionService(_stripeClient);
            var subOptions = new SubscriptionCreateOptions
            {
                Customer = request.CustomerId,
                DefaultPaymentMethod = request.PaymentMethodToken,
                Items = new List<SubscriptionItemOptions>
                {
                    new() { Plan = request.PlanReference }
                },
                Metadata = request.Metadata?.ToDictionary(k => k.Key, v => v.Value),
                BillingCycleAnchor = request.StartAt,
                TrialPeriodDays = request.TrialDays
            };

            var sub = await service.CreateAsync(subOptions, requestOptions, ct).ConfigureAwait(false);
            _logger.LogInformation("Stripe Subscription created: {SubId} plan={PlanId} customer={CustomerId} status={Status}",
                sub.Id, request.PlanReference, request.CustomerId, sub.Status);

            return Map(sub);
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "CreateSubscription");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    /// <inheritdoc />
    public async Task<Subscription?> GetSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);

        try
        {
            var service = new SubscriptionService(_stripeClient);
            var sub = await service.GetAsync(subscriptionReference, cancellationToken: ct).ConfigureAwait(false);
            return Map(sub);
        }
        catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "GetSubscription");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    /// <inheritdoc />
    public async Task<Subscription> CancelSubscriptionAsync(string subscriptionReference, bool immediately = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);

        try
        {
            var service = new SubscriptionService(_stripeClient);
            StripeSubscription sub;
            if (immediately)
            {
                sub = await service.CancelAsync(subscriptionReference, cancellationToken: ct).ConfigureAwait(false);
            }
            else
            {
                // Schedule the cancellation at the end of the current billing period.
                sub = await service.UpdateAsync(subscriptionReference,
                    new SubscriptionUpdateOptions { CancelAtPeriodEnd = true },
                    cancellationToken: ct).ConfigureAwait(false);
            }
            _logger.LogInformation("Stripe Subscription cancelled: {SubId} immediately={Immediately} status={Status}",
                sub.Id, immediately, sub.Status);
            return Map(sub);
        }
        catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Idempotency contract: already-cancelled subs are not an error.
            return new Subscription
            {
                Reference = subscriptionReference,
                PlanReference = string.Empty,
                CustomerId = string.Empty,
                Status = SubscriptionStatus.Cancelled,
                StartedAt = DateTime.UtcNow,
                CancelledAt = DateTime.UtcNow
            };
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "CancelSubscription");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    /// <inheritdoc />
    public async Task<Subscription> PauseSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);

        try
        {
            var service = new SubscriptionService(_stripeClient);
            var sub = await service.UpdateAsync(subscriptionReference, new SubscriptionUpdateOptions
            {
                PauseCollection = new SubscriptionPauseCollectionOptions { Behavior = "mark_uncollectible" }
            }, cancellationToken: ct).ConfigureAwait(false);
            return Map(sub);
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "PauseSubscription");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    /// <inheritdoc />
    public async Task<Subscription> ResumeSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);

        try
        {
            var service = new SubscriptionService(_stripeClient);
            // For un-paused (collection-paused) subs the dedicated Resume endpoint applies. For subs
            // that were paused via PauseCollection only, clearing the field via an Update is the
            // correct path. We try Resume first and fall back to Update.
            try
            {
                var resumed = await service.ResumeAsync(subscriptionReference, cancellationToken: ct).ConfigureAwait(false);
                return Map(resumed);
            }
            catch (StripeException resumeEx) when (resumeEx.HttpStatusCode is System.Net.HttpStatusCode.BadRequest)
            {
                var sub = await service.UpdateAsync(subscriptionReference,
                    new SubscriptionUpdateOptions { PauseCollection = null },
                    cancellationToken: ct).ConfigureAwait(false);
                return Map(sub);
            }
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "ResumeSubscription");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    private static Plan Map(global::Stripe.Plan plan, PlanRequest? source = null) => new()
    {
        Reference = plan.Id,
        Name = plan.Nickname ?? source?.Name ?? plan.Id,
        Amount = (plan.Amount ?? 0L) / 100m,
        Currency = (plan.Currency ?? source?.Currency ?? "usd").ToUpperInvariant(),
        Interval = ParseInterval(plan.Interval, (int)plan.IntervalCount),
        TotalCycles = source?.TotalCycles,
        Description = source?.Description
    };

    private static Subscription Map(StripeSubscription sub) => new()
    {
        Reference = sub.Id,
        PlanReference = sub.Items?.Data?.FirstOrDefault()?.Plan?.Id ?? sub.Items?.Data?.FirstOrDefault()?.Price?.Id ?? string.Empty,
        CustomerId = sub.CustomerId ?? string.Empty,
        Status = MapStatus(sub.Status),
        StartedAt = sub.StartDate,
        NextBillingAt = sub.CurrentPeriodEnd == default ? null : sub.CurrentPeriodEnd,
        CancelledAt = sub.CanceledAt,
        CyclesCompleted = 0 // Stripe doesn't expose a direct cycle counter; consumers can derive from invoices if needed.
    };

    private static SubscriptionStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "active" => SubscriptionStatus.Active,
        "paused" => SubscriptionStatus.Paused,
        "trialing" => SubscriptionStatus.Trialing,
        "past_due" or "unpaid" or "incomplete" => SubscriptionStatus.PastDue,
        "canceled" or "cancelled" or "incomplete_expired" => SubscriptionStatus.Cancelled,
        "ended" => SubscriptionStatus.Expired,
        _ => SubscriptionStatus.Active
    };

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

    private static long IntervalCount(SubscriptionInterval interval) => interval switch
    {
        SubscriptionInterval.BiWeekly => 2,
        SubscriptionInterval.Quarterly => 3,
        SubscriptionInterval.BiAnnually => 6,
        _ => 1
    };

    private static SubscriptionInterval ParseInterval(string? interval, int count) => (interval?.ToLowerInvariant(), count) switch
    {
        ("day", _) => SubscriptionInterval.Daily,
        ("week", 1) => SubscriptionInterval.Weekly,
        ("week", 2) => SubscriptionInterval.BiWeekly,
        ("month", 1) => SubscriptionInterval.Monthly,
        ("month", 3) => SubscriptionInterval.Quarterly,
        ("month", 6) => SubscriptionInterval.BiAnnually,
        ("year", _) => SubscriptionInterval.Annually,
        _ => SubscriptionInterval.Monthly
    };

    // Stripe statement descriptors are capped at 22 chars and may not contain certain symbols.
    // Best-effort truncation; consumer can override via Metadata.
    private static string TruncateStatementDescriptor(string name)
    {
        var safe = new string(name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '.').ToArray());
        return safe.Length > 22 ? safe[..22] : safe;
    }

    private static RequestOptions? BuildRequestOptions(string? idempotencyKey) =>
        string.IsNullOrEmpty(idempotencyKey) ? null : new RequestOptions { IdempotencyKey = idempotencyKey };

    private BhenguPaymentException TranslateException(StripeException ex, string operation)
    {
        var httpStatus = (int)ex.HttpStatusCode;
        var errorCode = ex.StripeError?.Code ?? ex.HttpStatusCode.ToString();
        var errorMessage = ex.StripeError?.Message ?? ex.Message;

        _logger.LogError(ex, "Stripe {Operation} failed: {HttpStatus} {Code} {Message}",
            operation, httpStatus, errorCode, errorMessage);

        if (httpStatus == 429)
            return new ProviderRateLimitException(ProviderName, providerErrorMessage: errorMessage, innerException: ex);

        if (httpStatus is >= 400 and < 500)
            return new PaymentDeclinedException(ProviderName, errorCode, errorMessage, ex);

        return new ProviderUnavailableException(ProviderName, $"HTTP {httpStatus}: {errorMessage}", ex);
    }
}
