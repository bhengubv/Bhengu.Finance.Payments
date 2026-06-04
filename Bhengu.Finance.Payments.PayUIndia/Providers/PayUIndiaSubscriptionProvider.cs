// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.PayUIndia.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayUIndia.Providers;

/// <summary>
/// PayU India recurring billing provider. Wraps PayU India's <c>SI Plan</c> + <c>SI</c>
/// (Standing Instruction) API exposed under <c>merchant/postservice.php</c> with
/// <c>create_invoice</c>, <c>get_subscription</c> and <c>cancel_subscription</c> commands.
/// </summary>
/// <remarks>
/// PayU India does NOT expose a separate, re-usable Plan resource in its India-specific recurring
/// API — every standing instruction inlines period + amount + currency. To preserve the SDK's
/// <see cref="ISubscriptionProvider"/> shape the SDK caches plan definitions in-process and
/// returns a synthetic plan reference (<c>payu_plan_*</c>). <see cref="CreateSubscriptionAsync"/>
/// resolves the cached plan and inlines the recurring block.
/// </remarks>
public sealed class PayUIndiaSubscriptionProvider : BhenguProviderBase, ISubscriptionProvider
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    // Plan cache is process-singleton so consumers don't have to re-create plans on every request.
    private static readonly ConcurrentDictionary<string, Plan> PlanCache = new(StringComparer.Ordinal);

    private readonly HttpClient _httpClient;
    private readonly PayUIndiaOptions _options;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.PayUIndia;

    /// <summary>Create a new PayU India subscription provider bound to the supplied HTTP client and options.</summary>
    public PayUIndiaSubscriptionProvider(
        HttpClient httpClient,
        IOptions<PayUIndiaOptions> options,
        ILogger<PayUIndiaSubscriptionProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.MerchantKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayUIndiaOptions.MerchantKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.Salt))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayUIndiaOptions.Salt)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.InfoBaseUrl ?? "https://info.payu.in/");
    }

    /// <inheritdoc />
    public Task<Plan> CreatePlanAsync(PlanRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "subscription.create_plan");

        // PayU India's SI API doesn't have a re-usable Plan entity — cache locally and synthesise an id.
        var reference = $"payu_plan_{Guid.NewGuid():N}";
        var plan = new Plan
        {
            Reference = reference,
            Name = request.Name,
            Amount = request.Amount,
            Currency = request.Currency.ToUpperInvariant(),
            Interval = request.Interval,
            TotalCycles = request.TotalCycles,
            Description = request.Description
        };

        PlanCache[reference] = plan;
        Logger.LogInformation("PayU India plan cached locally as {PlanId} ({Amount} {Currency}/{Interval})",
            reference, plan.Amount, plan.Currency, plan.Interval);

        activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
        return Task.FromResult(plan);
    }

    /// <inheritdoc />
    public Task<Plan?> GetPlanAsync(string planReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planReference);
        return Task.FromResult(PlanCache.TryGetValue(planReference, out var plan) ? plan : null);
    }

    /// <inheritdoc />
    public async Task<Subscription> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "subscription.create");
        try
        {
            if (!PlanCache.TryGetValue(request.PlanReference, out var plan))
                throw new BhenguPaymentException(ProviderName, $"Plan {request.PlanReference} not found in local cache");

            const string command = "create_invoice";
            var siRef = $"si_{Guid.NewGuid():N}";
            var amount = plan.Amount.ToString("F2", CultureInfo.InvariantCulture);

            var hashInput = string.Join("|", _options.MerchantKey, command, siRef, _options.Salt);
            var hash = Sha512Hex(hashInput);

            var form = new Dictionary<string, string>
            {
                ["key"] = _options.MerchantKey,
                ["command"] = command,
                ["var1"] = siRef,
                ["var2"] = request.CustomerId,
                ["var3"] = request.PaymentMethodToken,
                ["var4"] = amount,
                ["var5"] = plan.Currency,
                ["var6"] = MapBillingCycle(plan.Interval),
                ["var7"] = (plan.TotalCycles ?? 0).ToString(CultureInfo.InvariantCulture),
                ["hash"] = hash
            };

            var raw = await PostFormAsync("merchant/postservice.php?form=2", form, ct, "CreateSubscription").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PayUIndiaSubscriptionResponse>(raw, DeserializeOptions);

            Logger.LogInformation("PayU India subscription created: siRef={SiRef} status={Status}",
                siRef, response?.Status);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            return new Subscription
            {
                Reference = response?.SiId ?? siRef,
                PlanReference = request.PlanReference,
                CustomerId = request.CustomerId,
                Status = SubscriptionStatus.Active,
                StartedAt = request.StartAt ?? DateTime.UtcNow,
                NextBillingAt = ComputeNextBilling(request.StartAt ?? DateTime.UtcNow, plan.Interval),
                CyclesCompleted = 0
            };
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Subscription?> GetSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionReference);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "subscription.get");
        try
        {
            const string command = "get_subscription";
            var hashInput = string.Join("|", _options.MerchantKey, command, subscriptionReference, _options.Salt);
            var hash = Sha512Hex(hashInput);

            var form = new Dictionary<string, string>
            {
                ["key"] = _options.MerchantKey,
                ["command"] = command,
                ["var1"] = subscriptionReference,
                ["hash"] = hash
            };

            var raw = await PostFormAsync("merchant/postservice.php?form=2", form, ct, "GetSubscription").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PayUIndiaSubscriptionResponse>(raw, DeserializeOptions);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            if (response is null || string.IsNullOrEmpty(response.SiId))
                return null;

            return new Subscription
            {
                Reference = response.SiId,
                PlanReference = response.PlanId ?? string.Empty,
                CustomerId = response.CustomerId ?? string.Empty,
                Status = MapStatus(response.Status),
                StartedAt = response.CreatedAt ?? DateTime.UtcNow,
                NextBillingAt = response.NextBillingAt,
                CyclesCompleted = response.CyclesCompleted ?? 0
            };
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Subscription> CancelSubscriptionAsync(string subscriptionReference, bool immediately = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionReference);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "subscription.cancel");
        try
        {
            const string command = "cancel_subscription";
            var hashInput = string.Join("|", _options.MerchantKey, command, subscriptionReference, _options.Salt);
            var hash = Sha512Hex(hashInput);

            var form = new Dictionary<string, string>
            {
                ["key"] = _options.MerchantKey,
                ["command"] = command,
                ["var1"] = subscriptionReference,
                ["var2"] = immediately ? "1" : "0",
                ["hash"] = hash
            };

            await PostFormAsync("merchant/postservice.php?form=2", form, ct, "CancelSubscription").ConfigureAwait(false);

            Logger.LogInformation("PayU India subscription cancelled: siRef={SiRef} immediately={Immediately}",
                subscriptionReference, immediately);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

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
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<Subscription> PauseSubscriptionAsync(string subscriptionReference, CancellationToken ct = default) =>
        throw new BhenguPaymentException(ProviderName, "PayU India does not support pausing recurring subscriptions");

    /// <inheritdoc />
    public Task<Subscription> ResumeSubscriptionAsync(string subscriptionReference, CancellationToken ct = default) =>
        throw new BhenguPaymentException(ProviderName, "PayU India does not support resuming recurring subscriptions");

    private static string MapBillingCycle(SubscriptionInterval interval) => interval switch
    {
        SubscriptionInterval.Daily => "daily",
        SubscriptionInterval.Weekly => "weekly",
        SubscriptionInterval.BiWeekly => "weekly",
        SubscriptionInterval.Monthly => "monthly",
        SubscriptionInterval.Quarterly => "monthly",
        SubscriptionInterval.BiAnnually => "monthly",
        SubscriptionInterval.Annually => "yearly",
        _ => "monthly"
    };

    private static DateTime ComputeNextBilling(DateTime start, SubscriptionInterval interval) => interval switch
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

    private static SubscriptionStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "active" or "1" => SubscriptionStatus.Active,
        "paused" => SubscriptionStatus.Paused,
        "cancelled" or "canceled" => SubscriptionStatus.Cancelled,
        "expired" or "completed" => SubscriptionStatus.Expired,
        "past_due" or "failed" => SubscriptionStatus.PastDue,
        _ => SubscriptionStatus.Active
    };

    private async Task<string> PostFormAsync(string path, IDictionary<string, string> form, CancellationToken ct, string operation)
    {
        using var content = new FormUrlEncodedContent(form);
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to PayU India failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("PayU India {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static string Sha512Hex(string input)
    {
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed class PayUIndiaSubscriptionResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("si_id")] public string? SiId { get; set; }
        [JsonPropertyName("plan_id")] public string? PlanId { get; set; }
        [JsonPropertyName("customer_id")] public string? CustomerId { get; set; }
        [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }
        [JsonPropertyName("next_billing_at")] public DateTime? NextBillingAt { get; set; }
        [JsonPropertyName("cycles_completed")] public int? CyclesCompleted { get; set; }
    }
}
