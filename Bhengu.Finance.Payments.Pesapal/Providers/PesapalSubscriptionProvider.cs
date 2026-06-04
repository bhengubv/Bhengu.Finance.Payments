// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Pesapal.Configuration;
using Bhengu.Finance.Payments.Pesapal.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Pesapal.Providers;

/// <summary>
/// Pesapal subscription provider. Pesapal API 3.0 supports recurring billing via the
/// <c>SubmitOrderRequest</c> with <c>account_number</c> and <c>subscription_details</c> fields.
/// <para>
/// <b>Plan model:</b> Pesapal has no first-class "create plan" REST endpoint — plans are
/// templates the merchant tracks client-side. <see cref="CreatePlanAsync"/> persists the plan
/// definition to the injected <see cref="IBhenguDistributedCache"/> with a long TTL so subsequent
/// <see cref="CreateSubscriptionAsync"/> calls can read the cadence and amount without DB plumbing.
/// </para>
/// <para>
/// <b>Subscription creation:</b> issues a <c>SubmitOrderRequest</c> with subscription_details set
/// to the plan's interval and end-date (TotalCycles × Interval).
/// </para>
/// <para>
/// <b>Pause / resume:</b> not natively supported by Pesapal — both throw.
/// </para>
/// </summary>
public sealed class PesapalSubscriptionProvider : BhenguProviderBase, ISubscriptionProvider
{
    private static readonly TimeSpan PlanCacheTtl = TimeSpan.FromDays(90);
    private static readonly ConcurrentDictionary<string, Plan> s_planFallback = new(StringComparer.Ordinal);

    private readonly HttpClient _httpClient;
    private readonly PesapalOptions _options;
    private readonly IBhenguDistributedCache? _cache;
    private readonly PesapalTokenCache _tokenCache;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Pesapal;

    /// <summary>Construct the Pesapal subscription provider. Designed to be registered via DI.</summary>
    public PesapalSubscriptionProvider(
        HttpClient httpClient,
        IOptions<PesapalOptions> options,
        ILogger<PesapalSubscriptionProvider> logger,
        IBhenguDistributedCache? cache = null,
        PesapalTokenCache? tokenCache = null)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _cache = cache;
        _tokenCache = tokenCache ?? new PesapalTokenCache();

        if (string.IsNullOrWhiteSpace(_options.ConsumerKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PesapalOptions.ConsumerKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.ConsumerSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PesapalOptions.ConsumerSecret)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var raw = _options.UseSandbox
                ? (_options.SandboxUrl ?? "https://cybqa.pesapal.com/pesapalv3")
                : (_options.BaseUrl ?? "https://pay.pesapal.com/v3");
            if (!raw.EndsWith('/')) raw += "/";
            _httpClient.BaseAddress = new Uri(raw);
        }
    }

    /// <inheritdoc/>
    public async Task<Plan> CreatePlanAsync(PlanRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "subscription.create_plan");
        try
        {
            var reference = $"plan-{Guid.NewGuid():N}";
            var plan = new Plan
            {
                Reference = reference,
                Name = request.Name,
                Amount = request.Amount,
                Currency = request.Currency,
                Interval = request.Interval,
                TotalCycles = request.TotalCycles,
                Description = request.Description
            };
            await StorePlanAsync(plan, ct).ConfigureAwait(false);
            Logger.LogInformation("Pesapal plan stored: {Reference} name={Name}", plan.Reference, plan.Name);
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return plan;
        }
        catch (Exception ex)
        {
            activity.SetOutcome(ClassifyOutcome(ex));
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<Plan?> GetPlanAsync(string planReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(planReference);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "subscription.get_plan");
        try
        {
            var plan = await ReadPlanAsync(planReference, ct).ConfigureAwait(false);
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return plan;
        }
        catch (Exception ex)
        {
            activity.SetOutcome(ClassifyOutcome(ex));
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<Subscription> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "subscription.create");
        try
        {
            var plan = await ReadPlanAsync(request.PlanReference, ct).ConfigureAwait(false)
                ?? throw new BhenguPaymentException(ProviderName,
                    $"Pesapal subscription requires a plan stored via CreatePlanAsync first; {request.PlanReference} not found.");

            if (string.IsNullOrWhiteSpace(_options.IpnId))
                throw new ProviderConfigurationException(ProviderName, $"{nameof(PesapalOptions.IpnId)} is required");

            var start = (request.StartAt ?? DateTime.UtcNow).Date;
            var totalCycles = plan.TotalCycles ?? 12;
            var endDate = MapEndDate(start, plan.Interval, totalCycles);
            var frequency = MapFrequency(plan.Interval);
            var orderId = request.IdempotencyKey ?? $"sub-{Guid.NewGuid():N}";

            var body = new
            {
                id = orderId,
                currency = plan.Currency.ToUpperInvariant(),
                amount = plan.Amount,
                description = plan.Description ?? plan.Name,
                callback_url = _options.CallbackUrl,
                notification_id = _options.IpnId,
                account_number = request.CustomerId,
                subscription_details = new
                {
                    start_date = start.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
                    end_date = endDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
                    frequency
                },
                billing_address = new
                {
                    email_address = request.CustomerId
                }
            };

            var token = await PesapalHttpClient.EnsureTokenAsync(_httpClient, Logger, _options, _tokenCache, ct).ConfigureAwait(false);
            var responseBody = await PesapalHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Post, "api/Transactions/SubmitOrderRequest", body, token, ct, "CreateSubscription").ConfigureAwait(false);
            var submit = JsonSerializer.Deserialize<PesapalSubmitResponse>(responseBody);

            Logger.LogInformation("Pesapal subscription created: tracking={Tracking} plan={Plan}",
                submit?.OrderTrackingId, plan.Reference);

            var sub = new Subscription
            {
                Reference = submit?.OrderTrackingId ?? orderId,
                PlanReference = plan.Reference,
                CustomerId = request.CustomerId,
                Status = SubscriptionStatus.Active,
                StartedAt = start,
                NextBillingAt = MapNextBilling(start, plan.Interval),
                CyclesCompleted = 0
            };
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return sub;
        }
        catch (Exception ex)
        {
            activity.SetOutcome(ClassifyOutcome(ex));
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<Subscription?> GetSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "subscription.get");
        try
        {
            var path = $"api/Transactions/GetTransactionStatus?orderTrackingId={Uri.EscapeDataString(subscriptionReference)}";
            var token = await PesapalHttpClient.EnsureTokenAsync(_httpClient, Logger, _options, _tokenCache, ct).ConfigureAwait(false);
            var responseBody = await PesapalHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Get, path, body: null, token, ct, "GetSubscription").ConfigureAwait(false);
            var status = JsonSerializer.Deserialize<PesapalTransactionStatus>(responseBody);
            if (status is null)
            {
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
                return null;
            }

            var sub = new Subscription
            {
                Reference = subscriptionReference,
                PlanReference = string.Empty,
                CustomerId = status.MerchantReference ?? string.Empty,
                Status = MapSubStatus(status.StatusCode, status.PaymentStatusDescription),
                StartedAt = status.CreatedDate ?? DateTime.UtcNow,
                CyclesCompleted = 0
            };
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return sub;
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return null;
        }
        catch (Exception ex)
        {
            activity.SetOutcome(ClassifyOutcome(ex));
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<Subscription> CancelSubscriptionAsync(string subscriptionReference, bool immediately = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "subscription.cancel");
        try
        {
            var body = new { order_tracking_id = subscriptionReference };
            var token = await PesapalHttpClient.EnsureTokenAsync(_httpClient, Logger, _options, _tokenCache, ct).ConfigureAwait(false);
            await PesapalHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Post, "api/Transactions/CancelOrder", body, token, ct, "CancelSubscription").ConfigureAwait(false);

            Logger.LogInformation("Pesapal subscription {Reference} cancelled (immediately={Immediately})", subscriptionReference, immediately);
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return new Subscription
            {
                Reference = subscriptionReference,
                PlanReference = string.Empty,
                CustomerId = string.Empty,
                Status = SubscriptionStatus.Cancelled,
                StartedAt = DateTime.UtcNow,
                CancelledAt = DateTime.UtcNow,
                CyclesCompleted = 0
            };
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            // Idempotent: cancelling a non-existent subscription returns a cancelled stub.
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return new Subscription
            {
                Reference = subscriptionReference,
                PlanReference = string.Empty,
                CustomerId = string.Empty,
                Status = SubscriptionStatus.Cancelled,
                StartedAt = DateTime.UtcNow,
                CancelledAt = DateTime.UtcNow,
                CyclesCompleted = 0
            };
        }
        catch (Exception ex)
        {
            activity.SetOutcome(ClassifyOutcome(ex));
            throw;
        }
    }

    /// <inheritdoc/>
    public Task<Subscription> PauseSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        throw new BhenguPaymentException(ProviderName,
            "Pesapal does not expose a pause-subscription API. Cancel and recreate instead.",
            providerErrorCode: "not_supported");
    }

    /// <inheritdoc/>
    public Task<Subscription> ResumeSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionReference);
        throw new BhenguPaymentException(ProviderName,
            "Pesapal does not expose a resume-subscription API.",
            providerErrorCode: "not_supported");
    }

    private async Task StorePlanAsync(Plan plan, CancellationToken ct)
    {
        if (_cache is not null)
        {
            await _cache.SetAsync($"pesapal:plan:{plan.Reference}", plan, PlanCacheTtl, ct).ConfigureAwait(false);
            return;
        }
        s_planFallback[plan.Reference] = plan;
    }

    private async Task<Plan?> ReadPlanAsync(string reference, CancellationToken ct)
    {
        if (_cache is not null)
        {
            var fromCache = await _cache.GetAsync<Plan>($"pesapal:plan:{reference}", ct).ConfigureAwait(false);
            if (fromCache is not null) return fromCache;
        }
        return s_planFallback.GetValueOrDefault(reference);
    }

    private static DateTime MapEndDate(DateTime start, SubscriptionInterval interval, int totalCycles) =>
        interval switch
        {
            SubscriptionInterval.Daily => start.AddDays(totalCycles),
            SubscriptionInterval.Weekly => start.AddDays(7 * totalCycles),
            SubscriptionInterval.BiWeekly => start.AddDays(14 * totalCycles),
            SubscriptionInterval.Monthly => start.AddMonths(totalCycles),
            SubscriptionInterval.Quarterly => start.AddMonths(3 * totalCycles),
            SubscriptionInterval.BiAnnually => start.AddMonths(6 * totalCycles),
            SubscriptionInterval.Annually => start.AddYears(totalCycles),
            _ => start.AddMonths(totalCycles)
        };

    private static string MapFrequency(SubscriptionInterval interval) => interval switch
    {
        SubscriptionInterval.Daily => "DAILY",
        SubscriptionInterval.Weekly => "WEEKLY",
        SubscriptionInterval.BiWeekly => "WEEKLY",
        SubscriptionInterval.Monthly => "MONTHLY",
        SubscriptionInterval.Quarterly => "QUARTERLY",
        SubscriptionInterval.BiAnnually => "MONTHLY",
        SubscriptionInterval.Annually => "ANNUAL",
        _ => "MONTHLY"
    };

    private static DateTime MapNextBilling(DateTime start, SubscriptionInterval interval) => interval switch
    {
        SubscriptionInterval.Daily => start.AddDays(1),
        SubscriptionInterval.Weekly => start.AddDays(7),
        SubscriptionInterval.BiWeekly => start.AddDays(14),
        SubscriptionInterval.Monthly => start.AddMonths(1),
        SubscriptionInterval.Quarterly => start.AddMonths(3),
        SubscriptionInterval.BiAnnually => start.AddMonths(6),
        SubscriptionInterval.Annually => start.AddYears(1),
        _ => start.AddMonths(1)
    };

    private static SubscriptionStatus MapSubStatus(int? code, string? description) => (code, description?.ToUpperInvariant()) switch
    {
        (1, _) or (_, "COMPLETED") => SubscriptionStatus.Active,
        (2, _) or (_, "FAILED") => SubscriptionStatus.PastDue,
        (3, _) or (_, "REVERSED") => SubscriptionStatus.Cancelled,
        (0, _) or (_, "INVALID") => SubscriptionStatus.Cancelled,
        _ => SubscriptionStatus.Active
    };

    private static string ClassifyOutcome(Exception ex) => ex switch
    {
        PaymentDeclinedException => BhenguPaymentDiagnostics.Outcomes.Declined,
        ProviderRateLimitException => BhenguPaymentDiagnostics.Outcomes.RateLimited,
        ProviderUnavailableException => BhenguPaymentDiagnostics.Outcomes.Unavailable,
        _ => BhenguPaymentDiagnostics.Outcomes.Error
    };

    private sealed class PesapalSubmitResponse
    {
        [JsonPropertyName("order_tracking_id")] public string? OrderTrackingId { get; set; }
        [JsonPropertyName("merchant_reference")] public string? MerchantReference { get; set; }
        [JsonPropertyName("redirect_url")] public string? RedirectUrl { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class PesapalTransactionStatus
    {
        [JsonPropertyName("payment_method")] public string? PaymentMethod { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("created_date")] public DateTime? CreatedDate { get; set; }
        [JsonPropertyName("confirmation_code")] public string? ConfirmationCode { get; set; }
        [JsonPropertyName("payment_status_description")] public string? PaymentStatusDescription { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("merchant_reference")] public string? MerchantReference { get; set; }
        [JsonPropertyName("status_code")] public int StatusCode { get; set; }
    }
}
