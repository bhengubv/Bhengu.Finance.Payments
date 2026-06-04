// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.PagSeguro.Configuration;
using Bhengu.Finance.Payments.PagSeguro.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PagSeguro.Providers;

/// <summary>
/// PagSeguro / PagBank recurring-billing provider. Wraps the <c>/recurring/orders</c> endpoint —
/// PagBank's model for inlined-plan recurring orders in Brazil.
/// </summary>
/// <remarks>
/// PagBank inlines the plan template into the recurring order: there is no separate plan resource.
/// To preserve the SDK's <c>ISubscriptionProvider</c> shape, plan templates are cached in-process
/// and a synthetic plan reference (GUID prefixed with <c>pg_plan_</c>) is returned. Subscription
/// creation looks the cached plan up and emits the inline recurring schedule.
/// <para>
/// PagBank does not currently expose pause/resume on a recurring order. Cancellation is performed
/// via <c>POST /recurring/orders/{id}/cancel</c> and is one-way.
/// </para>
/// </remarks>
public sealed class PagSeguroSubscriptionProvider : ISubscriptionProvider
{
    private const string PlanKeyPrefix = "pagseguro:plan:";
    private static readonly TimeSpan PlanTtl = TimeSpan.FromDays(365);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly PagSeguroOptions _options;
    private readonly ILogger<PagSeguroSubscriptionProvider> _logger;
    private readonly IBhenguDistributedCache _cache;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.PagSeguro;

    /// <summary>Create a new PagSeguro subscription provider bound to the supplied HTTP client, options, and distributed cache.</summary>
    public PagSeguroSubscriptionProvider(
        HttpClient httpClient,
        IOptions<PagSeguroOptions> options,
        ILogger<PagSeguroSubscriptionProvider> logger,
        IBhenguDistributedCache cache)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));

        if (string.IsNullOrWhiteSpace(_options.ApiToken))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PagSeguroOptions.ApiToken)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var resolved = _options.UseSandbox
                ? (_options.SandboxUrl ?? "https://sandbox.api.pagseguro.com")
                : (_options.BaseUrl ?? "https://api.pagseguro.com");
            _httpClient.BaseAddress = new Uri(resolved);
        }

        if (_httpClient.DefaultRequestHeaders.Authorization is null)
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
    }

    /// <summary>Back-compat constructor that uses the process-local in-memory cache.</summary>
    public PagSeguroSubscriptionProvider(
        HttpClient httpClient,
        IOptions<PagSeguroOptions> options,
        ILogger<PagSeguroSubscriptionProvider> logger)
        : this(httpClient, options, logger, new InMemoryBhenguDistributedCache())
    {
    }

    /// <inheritdoc />
    public Task<Plan> CreatePlanAsync(PlanRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PagSeguroObservability.ObserveAsync("create_plan", () => CreatePlanCoreAsync(request, ct));
    }

    private async Task<Plan> CreatePlanCoreAsync(PlanRequest request, CancellationToken ct)
    {
        var reference = "pg_plan_" + Guid.NewGuid().ToString("N");
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

        await _cache.SetAsync(PlanKeyPrefix + reference, plan, PlanTtl, ct).ConfigureAwait(false);
        _logger.LogInformation("PagSeguro plan cached: {Reference} name={Name}", reference, request.Name);

        return plan;
    }

    /// <inheritdoc />
    public Task<Plan?> GetPlanAsync(string planReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planReference);
        return PagSeguroObservability.ObserveAsync("get_plan", () =>
            _cache.GetAsync<Plan>(PlanKeyPrefix + planReference, ct));
    }

    /// <inheritdoc />
    public Task<Subscription> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PagSeguroObservability.ObserveAsync("create_subscription", () => CreateSubscriptionCoreAsync(request, ct));
    }

    private async Task<Subscription> CreateSubscriptionCoreAsync(SubscriptionRequest request, CancellationToken ct)
    {
        var plan = await _cache.GetAsync<Plan>(PlanKeyPrefix + request.PlanReference, ct).ConfigureAwait(false)
            ?? throw new BhenguPaymentException(
                ProviderName,
                $"Plan reference '{request.PlanReference}' is not cached. Call CreatePlanAsync first.",
                providerErrorCode: "unknown_plan");

        var amountInCents = (long)(plan.Amount * 100);
        var (interval, intervalCount) = MapInterval(plan.Interval);
        var startDate = request.StartAt ?? DateTime.UtcNow;
        var customerEmail = request.Metadata?.GetValueOrDefault("customer_email")
            ?? throw new BhenguPaymentException(ProviderName, "Metadata['customer_email'] is required for PagBank recurring orders", providerErrorCode: "missing_customer_email");

        var body = new Dictionary<string, object?>
        {
            ["reference_id"] = request.IdempotencyKey ?? request.PlanReference,
            ["customer"] = new Dictionary<string, object?>
            {
                ["id"] = request.CustomerId,
                ["email"] = customerEmail,
                ["name"] = request.Metadata?.GetValueOrDefault("customer_name"),
                ["tax_id"] = request.Metadata?.GetValueOrDefault("customer_tax_id")
            },
            ["plan"] = new Dictionary<string, object?>
            {
                ["name"] = plan.Name,
                ["description"] = plan.Description,
                ["amount"] = new { value = amountInCents, currency = plan.Currency },
                ["interval"] = interval,
                ["interval_count"] = intervalCount,
                ["total_cycles"] = plan.TotalCycles
            },
            ["start_date"] = startDate.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture),
            ["payment_method"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "CREDIT_CARD",
                    ["card"] = new Dictionary<string, object?>
                    {
                        ["encrypted"] = request.PaymentMethodToken
                    }
                }
            },
            ["trial"] = request.TrialDays is > 0 ? new { days = request.TrialDays } : null,
            ["notification_urls"] = _options.NotificationUrl is null ? null : new[] { _options.NotificationUrl }
        };

        var raw = await SendAsync(HttpMethod.Post, "/recurring/orders", body, ct, "CreateSubscription").ConfigureAwait(false);
        var order = DeserialiseOrThrow<PagSeguroRecurringOrder>(raw, "CreateSubscription");

        _logger.LogInformation("PagSeguro recurring order created: {Id} status={Status}", order.Id, order.Status);
        return MapSubscription(order, request.CustomerId, request.PlanReference);
    }

    /// <inheritdoc />
    public Task<Subscription?> GetSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionReference);
        return PagSeguroObservability.ObserveAsync("get_subscription", () => GetSubscriptionCoreAsync(subscriptionReference, ct));
    }

    private async Task<Subscription?> GetSubscriptionCoreAsync(string subscriptionReference, CancellationToken ct)
    {
        try
        {
            var raw = await SendAsync(HttpMethod.Get, $"/recurring/orders/{Uri.EscapeDataString(subscriptionReference)}", body: null, ct, "GetSubscription").ConfigureAwait(false);
            var order = DeserialiseOrThrow<PagSeguroRecurringOrder>(raw, "GetSubscription");
            return MapSubscription(order, order.Customer?.Id ?? string.Empty, order.ReferenceId ?? string.Empty);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404" || ex.ProviderErrorCode == "400")
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task<Subscription> CancelSubscriptionAsync(string subscriptionReference, bool immediately = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionReference);
        return PagSeguroObservability.ObserveAsync("cancel_subscription", () => CancelSubscriptionCoreAsync(subscriptionReference, immediately, ct));
    }

    private async Task<Subscription> CancelSubscriptionCoreAsync(string subscriptionReference, bool immediately, CancellationToken ct)
    {
        try
        {
            var raw = await SendAsync(HttpMethod.Post, $"/recurring/orders/{Uri.EscapeDataString(subscriptionReference)}/cancel", body: new { }, ct, "CancelSubscription").ConfigureAwait(false);
            var order = DeserialiseOrThrow<PagSeguroRecurringOrder>(raw, "CancelSubscription");
            return MapSubscription(order, order.Customer?.Id ?? string.Empty, order.ReferenceId ?? string.Empty);
        }
        catch (PaymentDeclinedException)
        {
            // PagBank returns 4xx when re-cancelling. Treat as idempotent if already terminal.
            var existing = await GetSubscriptionAsync(subscriptionReference, ct).ConfigureAwait(false);
            if (existing is { Status: SubscriptionStatus.Cancelled or SubscriptionStatus.Expired })
                return existing;
            throw;
        }
    }

    /// <inheritdoc />
    public Task<Subscription> PauseSubscriptionAsync(string subscriptionReference, CancellationToken ct = default) =>
        PagSeguroObservability.ObserveAsync<Subscription>("pause_subscription", () =>
            throw new BhenguPaymentException(
                ProviderName,
                "PagBank does not support pausing recurring orders; cancel and re-create when the customer is ready.",
                providerErrorCode: "pause_not_supported"));

    /// <inheritdoc />
    public Task<Subscription> ResumeSubscriptionAsync(string subscriptionReference, CancellationToken ct = default) =>
        PagSeguroObservability.ObserveAsync<Subscription>("resume_subscription", () =>
            throw new BhenguPaymentException(
                ProviderName,
                "PagBank does not support resuming recurring orders; pause is unsupported, so resume is unsupported.",
                providerErrorCode: "resume_not_supported"));

    // === Helpers ===

    // PagBank uses an "interval" string (DAY/WEEK/MONTH/YEAR) plus an "interval_count" multiplier.
    private static (string Interval, int Count) MapInterval(SubscriptionInterval interval) => interval switch
    {
        SubscriptionInterval.Daily => ("DAY", 1),
        SubscriptionInterval.Weekly => ("WEEK", 1),
        SubscriptionInterval.BiWeekly => ("WEEK", 2),
        SubscriptionInterval.Monthly => ("MONTH", 1),
        SubscriptionInterval.Quarterly => ("MONTH", 3),
        SubscriptionInterval.BiAnnually => ("MONTH", 6),
        SubscriptionInterval.Annually => ("YEAR", 1),
        _ => ("MONTH", 1)
    };

    private static SubscriptionStatus MapStatus(string? raw) => raw?.ToUpperInvariant() switch
    {
        "ACTIVE" or "AUTHORIZED" or "PROCESSING" => SubscriptionStatus.Active,
        "TRIAL" or "TRIALING" => SubscriptionStatus.Trialing,
        "SUSPENDED" or "PAUSED" => SubscriptionStatus.Paused,
        "PAST_DUE" or "PASTDUE" => SubscriptionStatus.PastDue,
        "CANCELED" or "CANCELLED" => SubscriptionStatus.Cancelled,
        "EXPIRED" or "FINISHED" or "COMPLETED" => SubscriptionStatus.Expired,
        _ => SubscriptionStatus.Active
    };

    private static Subscription MapSubscription(PagSeguroRecurringOrder order, string customerId, string planReference)
    {
        var status = MapStatus(order.Status);
        return new Subscription
        {
            Reference = order.Id ?? string.Empty,
            PlanReference = string.IsNullOrEmpty(planReference) ? (order.ReferenceId ?? string.Empty) : planReference,
            CustomerId = string.IsNullOrEmpty(customerId) ? (order.Customer?.Id ?? string.Empty) : customerId,
            Status = status,
            StartedAt = TryParseDate(order.StartDate) ?? DateTime.UtcNow,
            NextBillingAt = TryParseDate(order.NextInvoiceAt),
            CancelledAt = status == SubscriptionStatus.Cancelled ? TryParseDate(order.UpdatedAt) : null,
            CyclesCompleted = order.Cycles?.Completed ?? 0
        };
    }

    private static DateTime? TryParseDate(string? value) =>
        DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
            ? dt
            : (DateTime?)null;

    private async Task<string> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string operation)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, WriteOptions);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to PagSeguro failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("PagSeguro {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static T DeserialiseOrThrow<T>(string raw, string operation)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(raw)
                ?? throw new BhenguPaymentException(ProviderNames.PagSeguro, $"PagSeguro {operation} returned empty body");
        }
        catch (JsonException ex)
        {
            throw new BhenguPaymentException(ProviderNames.PagSeguro, $"Failed to parse PagSeguro {operation} response", innerException: ex);
        }
    }

    // === PagSeguro API response shapes (internal) ===

    private sealed class PagSeguroRecurringOrder
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("reference_id")] public string? ReferenceId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("customer")] public PagSeguroCustomer? Customer { get; set; }
        [JsonPropertyName("plan")] public PagSeguroRecurringPlan? Plan { get; set; }
        [JsonPropertyName("start_date")] public string? StartDate { get; set; }
        [JsonPropertyName("next_invoice_at")] public string? NextInvoiceAt { get; set; }
        [JsonPropertyName("updated_at")] public string? UpdatedAt { get; set; }
        [JsonPropertyName("cycles")] public PagSeguroCycles? Cycles { get; set; }
    }

    private sealed class PagSeguroCustomer
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
    }

    private sealed class PagSeguroRecurringPlan
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("interval")] public string? Interval { get; set; }
        [JsonPropertyName("interval_count")] public int IntervalCount { get; set; }
        [JsonPropertyName("amount")] public PagSeguroPlanAmount? Amount { get; set; }
    }

    private sealed class PagSeguroPlanAmount
    {
        [JsonPropertyName("value")] public long Value { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }

    private sealed class PagSeguroCycles
    {
        [JsonPropertyName("completed")] public int Completed { get; set; }
        [JsonPropertyName("total")] public int? Total { get; set; }
    }
}
