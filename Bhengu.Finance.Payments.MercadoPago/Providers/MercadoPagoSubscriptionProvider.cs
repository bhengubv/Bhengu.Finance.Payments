// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.MercadoPago.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.MercadoPago.Providers;

/// <summary>
/// Mercado Pago recurring-billing provider. Wraps the <c>/preapproval</c> endpoint — Mercado Pago's
/// model for Preapproved Recurring Payments in Brazil/Latin America.
/// </summary>
/// <remarks>
/// Mercado Pago does NOT expose a separate plan resource in the Brazilian API: every subscription
/// inlines its <c>auto_recurring</c> block (frequency + amount + currency). To preserve the SDK's
/// <c>ISubscriptionProvider</c> shape, plan templates are cached in-process and a synthetic
/// plan reference (GUID prefixed with <c>mp_plan_</c>) is returned. Subscription creation looks the
/// cached plan up and emits the inline auto_recurring block.
/// <para>
/// Mercado Pago does not have native pause/resume on a preapproval beyond setting <c>status</c> to
/// <c>paused</c> (which stops collections) and back to <c>authorized</c>. Cancel is one-way.
/// </para>
/// </remarks>
public sealed class MercadoPagoSubscriptionProvider : ISubscriptionProvider
{
    // Plans are cached in-process. Singleton so the cache survives across requests within a host.
    private static readonly ConcurrentDictionary<string, Plan> PlanCache = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly MercadoPagoOptions _options;
    private readonly ILogger<MercadoPagoSubscriptionProvider> _logger;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.MercadoPago;

    /// <summary>Create a new Mercado Pago subscription provider bound to the supplied HTTP client and options.</summary>
    public MercadoPagoSubscriptionProvider(
        HttpClient httpClient,
        IOptions<MercadoPagoOptions> options,
        ILogger<MercadoPagoSubscriptionProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.AccessToken))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MercadoPagoOptions.AccessToken)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.mercadopago.com");

        if (_httpClient.DefaultRequestHeaders.Authorization is null)
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
    }

    /// <inheritdoc />
    public Task<Plan> CreatePlanAsync(PlanRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // No remote round-trip; the plan lives in-process until a subscription is created against it.
        var reference = "mp_plan_" + Guid.NewGuid().ToString("N");
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

        PlanCache[reference] = plan;
        _logger.LogInformation("Mercado Pago plan cached in-process: {Reference} name={Name}", reference, request.Name);

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

        if (!PlanCache.TryGetValue(request.PlanReference, out var plan))
            throw new BhenguPaymentException(
                ProviderName,
                $"Plan reference '{request.PlanReference}' is not cached. Mercado Pago requires the plan template to live in-process; call CreatePlanAsync first.",
                providerErrorCode: "unknown_plan");

        var (frequency, frequencyType) = MapInterval(plan.Interval);
        var startDate = request.StartAt ?? DateTime.UtcNow;
        DateTime? endDate = plan.TotalCycles is int cycles
            ? AddIntervals(startDate, plan.Interval, cycles)
            : (DateTime?)null;

        var body = new Dictionary<string, object?>
        {
            ["reason"] = plan.Description ?? plan.Name,
            ["external_reference"] = request.IdempotencyKey ?? request.PlanReference,
            ["payer_email"] = request.Metadata?.GetValueOrDefault("payer_email"),
            ["card_token_id"] = request.PaymentMethodToken,
            ["back_url"] = request.Metadata?.GetValueOrDefault("back_url"),
            ["status"] = "authorized",
            ["auto_recurring"] = new Dictionary<string, object?>
            {
                ["frequency"] = frequency,
                ["frequency_type"] = frequencyType,
                ["transaction_amount"] = plan.Amount,
                ["currency_id"] = plan.Currency,
                ["start_date"] = startDate.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture),
                ["end_date"] = endDate?.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture)
            }
        };

        var raw = await SendAsync(HttpMethod.Post, "/preapproval", body, ct, "CreateSubscription").ConfigureAwait(false);
        var pre = DeserialiseOrThrow<MercadoPagoPreapproval>(raw, "CreateSubscription");

        _logger.LogInformation("Mercado Pago preapproval created: {Id} status={Status}", pre.Id, pre.Status);
        return MapSubscription(pre, request.CustomerId, request.PlanReference);
    }

    /// <inheritdoc />
    public async Task<Subscription?> GetSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionReference);

        try
        {
            var raw = await SendAsync(HttpMethod.Get, $"/preapproval/{Uri.EscapeDataString(subscriptionReference)}", body: null, ct, "GetSubscription").ConfigureAwait(false);
            var pre = DeserialiseOrThrow<MercadoPagoPreapproval>(raw, "GetSubscription");
            return MapSubscription(pre, customerId: pre.PayerId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, planReference: pre.PreapprovalPlanId ?? string.Empty);
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

        try
        {
            var body = new Dictionary<string, object?> { ["status"] = "cancelled" };
            var raw = await SendAsync(HttpMethod.Put, $"/preapproval/{Uri.EscapeDataString(subscriptionReference)}", body, ct, "CancelSubscription").ConfigureAwait(false);
            var pre = DeserialiseOrThrow<MercadoPagoPreapproval>(raw, "CancelSubscription");
            return MapSubscription(pre, customerId: pre.PayerId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, planReference: pre.PreapprovalPlanId ?? string.Empty);
        }
        catch (PaymentDeclinedException)
        {
            // Mercado Pago returns 400/409 when re-cancelling. Treat as idempotent if the subscription
            // is already in a terminal state — otherwise rethrow so genuine 4xx surface.
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

        var body = new Dictionary<string, object?> { ["status"] = "paused" };
        var raw = await SendAsync(HttpMethod.Put, $"/preapproval/{Uri.EscapeDataString(subscriptionReference)}", body, ct, "PauseSubscription").ConfigureAwait(false);
        var pre = DeserialiseOrThrow<MercadoPagoPreapproval>(raw, "PauseSubscription");
        return MapSubscription(pre, customerId: pre.PayerId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, planReference: pre.PreapprovalPlanId ?? string.Empty);
    }

    /// <inheritdoc />
    public async Task<Subscription> ResumeSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionReference);

        var body = new Dictionary<string, object?> { ["status"] = "authorized" };
        var raw = await SendAsync(HttpMethod.Put, $"/preapproval/{Uri.EscapeDataString(subscriptionReference)}", body, ct, "ResumeSubscription").ConfigureAwait(false);
        var pre = DeserialiseOrThrow<MercadoPagoPreapproval>(raw, "ResumeSubscription");
        return MapSubscription(pre, customerId: pre.PayerId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, planReference: pre.PreapprovalPlanId ?? string.Empty);
    }

    // === Helpers ===

    // Mercado Pago accepts (frequency, frequency_type) — e.g. (1, "months") or (7, "days").
    private static (int Frequency, string FrequencyType) MapInterval(SubscriptionInterval interval) => interval switch
    {
        SubscriptionInterval.Daily => (1, "days"),
        SubscriptionInterval.Weekly => (7, "days"),
        SubscriptionInterval.BiWeekly => (14, "days"),
        SubscriptionInterval.Monthly => (1, "months"),
        SubscriptionInterval.Quarterly => (3, "months"),
        SubscriptionInterval.BiAnnually => (6, "months"),
        SubscriptionInterval.Annually => (12, "months"),
        _ => (1, "months")
    };

    private static DateTime AddIntervals(DateTime start, SubscriptionInterval interval, int cycles) => interval switch
    {
        SubscriptionInterval.Daily => start.AddDays(cycles),
        SubscriptionInterval.Weekly => start.AddDays(7 * cycles),
        SubscriptionInterval.BiWeekly => start.AddDays(14 * cycles),
        SubscriptionInterval.Monthly => start.AddMonths(cycles),
        SubscriptionInterval.Quarterly => start.AddMonths(3 * cycles),
        SubscriptionInterval.BiAnnually => start.AddMonths(6 * cycles),
        SubscriptionInterval.Annually => start.AddYears(cycles),
        _ => start.AddMonths(cycles)
    };

    private static SubscriptionStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "authorized" or "active" => SubscriptionStatus.Active,
        "pending" => SubscriptionStatus.Active,
        "paused" => SubscriptionStatus.Paused,
        "cancelled" or "canceled" => SubscriptionStatus.Cancelled,
        "finished" or "expired" => SubscriptionStatus.Expired,
        _ => SubscriptionStatus.Active
    };

    private static Subscription MapSubscription(MercadoPagoPreapproval pre, string customerId, string planReference)
    {
        var mappedStatus = MapStatus(pre.Status);
        var cancelledAt = mappedStatus == SubscriptionStatus.Cancelled ? TryParseDate(pre.LastModified) : null;

        return new Subscription
        {
            Reference = pre.Id ?? string.Empty,
            PlanReference = string.IsNullOrEmpty(planReference) ? (pre.PreapprovalPlanId ?? string.Empty) : planReference,
            CustomerId = customerId,
            Status = mappedStatus,
            StartedAt = TryParseDate(pre.DateCreated) ?? DateTime.UtcNow,
            NextBillingAt = TryParseDate(pre.NextPaymentDate),
            CancelledAt = cancelledAt,
            CyclesCompleted = pre.AutoRecurring?.PaymentsCount ?? 0
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

        if (method == HttpMethod.Post || method == HttpMethod.Put)
            req.Headers.TryAddWithoutValidation("X-Idempotency-Key", Guid.NewGuid().ToString("N"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Mercado Pago failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Mercado Pago {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
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
                ?? throw new BhenguPaymentException(ProviderNames.MercadoPago, $"Mercado Pago {operation} returned empty body");
        }
        catch (JsonException ex)
        {
            throw new BhenguPaymentException(ProviderNames.MercadoPago, $"Failed to parse Mercado Pago {operation} response", innerException: ex);
        }
    }

    // === Mercado Pago API response shapes (internal) ===

    private sealed class MercadoPagoPreapproval
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("preapproval_plan_id")] public string? PreapprovalPlanId { get; set; }
        [JsonPropertyName("payer_id")] public long? PayerId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
        [JsonPropertyName("external_reference")] public string? ExternalReference { get; set; }
        [JsonPropertyName("date_created")] public string? DateCreated { get; set; }
        [JsonPropertyName("last_modified")] public string? LastModified { get; set; }
        [JsonPropertyName("next_payment_date")] public string? NextPaymentDate { get; set; }
        [JsonPropertyName("auto_recurring")] public MercadoPagoAutoRecurring? AutoRecurring { get; set; }
    }

    private sealed class MercadoPagoAutoRecurring
    {
        [JsonPropertyName("frequency")] public int Frequency { get; set; }
        [JsonPropertyName("frequency_type")] public string? FrequencyType { get; set; }
        [JsonPropertyName("transaction_amount")] public decimal TransactionAmount { get; set; }
        [JsonPropertyName("currency_id")] public string? CurrencyId { get; set; }
        [JsonPropertyName("start_date")] public string? StartDate { get; set; }
        [JsonPropertyName("end_date")] public string? EndDate { get; set; }
        [JsonPropertyName("payments_count")] public int PaymentsCount { get; set; }
    }
}
