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
using Bhengu.Finance.Payments.Paytm.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Paytm.Providers;

/// <summary>
/// Paytm Subscription API provider. Wraps Paytm's <c>theia/api/v1/subscription/create</c>,
/// <c>fetchSubscription</c>, and <c>cancelSubscription</c> endpoints used by Paytm All-in-One
/// merchants for UPI-Autopay and card-on-file recurring billing.
/// </summary>
/// <remarks>
/// Paytm doesn't expose a separate Plan resource; every subscription inlines its amount + frequency.
/// To preserve the SDK's <see cref="ISubscriptionProvider"/> shape we cache plans locally and
/// inline them at subscription-creation time.
/// </remarks>
public sealed class PaytmSubscriptionProvider : BhenguProviderBase, ISubscriptionProvider
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions SerializeOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly ConcurrentDictionary<string, Plan> PlanCache = new(StringComparer.Ordinal);

    private readonly HttpClient _httpClient;
    private readonly PaytmOptions _options;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Paytm;

    /// <summary>Create a new Paytm subscription provider.</summary>
    public PaytmSubscriptionProvider(
        HttpClient httpClient,
        IOptions<PaytmOptions> options,
        ILogger<PaytmSubscriptionProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaytmOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaytmOptions.MerchantKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(_options.UseSandbox
                ? (_options.SandboxUrl ?? "https://securegw-stage.paytm.in/")
                : (_options.BaseUrl ?? "https://securegw.paytm.in/"));
        }
    }

    /// <inheritdoc />
    public Task<Plan> CreatePlanAsync(PlanRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunOperationAsync("create_plan", () =>
        {
            var reference = $"paytm_plan_{Guid.NewGuid():N}";
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
            Logger.LogInformation("Paytm plan cached as {Ref} ({Amount} {Currency}/{Interval})",
                reference, plan.Amount, plan.Currency, plan.Interval);

            return Task.FromResult(plan);
        }, ct);
    }

    /// <inheritdoc />
    public Task<Plan?> GetPlanAsync(string planReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planReference);
        return Task.FromResult(PlanCache.TryGetValue(planReference, out var p) ? p : null);
    }

    /// <inheritdoc />
    public Task<Subscription> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunOperationAsync("create_subscription", async () =>
        {
            if (!PlanCache.TryGetValue(request.PlanReference, out var plan))
                throw new BhenguPaymentException(ProviderName, $"Plan {request.PlanReference} not found in local cache");

            var subscriptionId = $"SUB_{Guid.NewGuid():N}";
            var amount = plan.Amount.ToString("F2", CultureInfo.InvariantCulture);

            var bodyPayload = new
            {
                requestType = "subscription",
                mid = _options.MerchantId,
                orderId = subscriptionId,
                callbackUrl = _options.CallbackUrl,
                txnAmount = new { value = amount, currency = plan.Currency },
                subscriptionAmountType = "FIX",
                subscriptionFrequency = "1",
                subscriptionFrequencyUnit = MapFrequencyUnit(plan.Interval),
                subscriptionStartDate = (request.StartAt ?? DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                subscriptionExpiryDate = plan.TotalCycles is > 0
                    ? ((request.StartAt ?? DateTime.UtcNow).AddDays(IntervalDays(plan.Interval) * plan.TotalCycles.Value)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : (string?)null,
                userInfo = new
                {
                    custId = request.CustomerId,
                    paymentMethodToken = request.PaymentMethodToken
                }
            };

            var serializedBody = JsonSerializer.Serialize(bodyPayload, SerializeOptions);
            var signature = ComputeChecksum(serializedBody);
            var envelope = new { body = bodyPayload, head = new { signature } };

            var raw = await SendAsync(HttpMethod.Post, $"subscription/create?mid={Uri.EscapeDataString(_options.MerchantId)}&orderId={Uri.EscapeDataString(subscriptionId)}", envelope, ct, "CreateSubscription").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PaytmSubscriptionEnvelope<PaytmSubscriptionBody>>(raw, DeserializeOptions);

            Logger.LogInformation("Paytm subscription created: {Id} status={Status}",
                subscriptionId, response?.Body?.ResultInfo?.ResultStatus);

            return new Subscription
            {
                Reference = response?.Body?.SubscriptionId ?? subscriptionId,
                PlanReference = request.PlanReference,
                CustomerId = request.CustomerId,
                Status = SubscriptionStatus.Active,
                StartedAt = request.StartAt ?? DateTime.UtcNow,
                NextBillingAt = ComputeNext(request.StartAt ?? DateTime.UtcNow, plan.Interval),
                CyclesCompleted = 0
            };
        }, ct);
    }

    /// <inheritdoc />
    public Task<Subscription?> GetSubscriptionAsync(string subscriptionReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionReference);
        return RunOperationAsync<Subscription?>("get_subscription", async () =>
        {
            try
            {
                var bodyPayload = new { mid = _options.MerchantId, subscriptionId = subscriptionReference };
                var serializedBody = JsonSerializer.Serialize(bodyPayload, SerializeOptions);
                var signature = ComputeChecksum(serializedBody);
                var envelope = new { body = bodyPayload, head = new { signature } };

                var raw = await SendAsync(HttpMethod.Post, "subscription/fetchSubscription", envelope, ct, "FetchSubscription").ConfigureAwait(false);
                var response = JsonSerializer.Deserialize<PaytmSubscriptionEnvelope<PaytmSubscriptionBody>>(raw, DeserializeOptions);

                if (response?.Body?.SubscriptionId is null)
                    return null;

                return new Subscription
                {
                    Reference = response.Body.SubscriptionId,
                    PlanReference = response.Body.PlanId ?? string.Empty,
                    CustomerId = response.Body.CustomerId ?? string.Empty,
                    Status = MapStatus(response.Body.Status),
                    StartedAt = response.Body.StartDate ?? DateTime.UtcNow,
                    NextBillingAt = response.Body.NextBillingDate,
                    CyclesCompleted = response.Body.CyclesCompleted ?? 0
                };
            }
            catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
            {
                return null;
            }
        }, ct);
    }

    /// <inheritdoc />
    public Task<Subscription> CancelSubscriptionAsync(string subscriptionReference, bool immediately = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionReference);
        return RunOperationAsync("cancel_subscription", async () =>
        {
            var bodyPayload = new
            {
                mid = _options.MerchantId,
                subscriptionId = subscriptionReference,
                cancelMode = immediately ? "IMMEDIATE" : "AT_PERIOD_END"
            };
            var serializedBody = JsonSerializer.Serialize(bodyPayload, SerializeOptions);
            var signature = ComputeChecksum(serializedBody);
            var envelope = new { body = bodyPayload, head = new { signature } };

            await SendAsync(HttpMethod.Post, "subscription/cancelSubscription", envelope, ct, "CancelSubscription").ConfigureAwait(false);

            Logger.LogInformation("Paytm subscription cancelled: {Id} immediately={Immediately}", subscriptionReference, immediately);

            return new Subscription
            {
                Reference = subscriptionReference,
                PlanReference = string.Empty,
                CustomerId = string.Empty,
                Status = SubscriptionStatus.Cancelled,
                StartedAt = DateTime.UtcNow,
                CancelledAt = DateTime.UtcNow
            };
        }, ct);
    }

    /// <inheritdoc />
    public Task<Subscription> PauseSubscriptionAsync(string subscriptionReference, CancellationToken ct = default) =>
        throw new BhenguPaymentException(ProviderName, "Paytm does not support pausing recurring subscriptions");

    /// <inheritdoc />
    public Task<Subscription> ResumeSubscriptionAsync(string subscriptionReference, CancellationToken ct = default) =>
        throw new BhenguPaymentException(ProviderName, "Paytm does not support resuming recurring subscriptions");

    private static string MapFrequencyUnit(SubscriptionInterval i) => i switch
    {
        SubscriptionInterval.Daily => "DAY",
        SubscriptionInterval.Weekly or SubscriptionInterval.BiWeekly => "WEEK",
        SubscriptionInterval.Monthly or SubscriptionInterval.Quarterly or SubscriptionInterval.BiAnnually => "MONTH",
        SubscriptionInterval.Annually => "YEAR",
        _ => "MONTH"
    };

    private static int IntervalDays(SubscriptionInterval i) => i switch
    {
        SubscriptionInterval.Daily => 1,
        SubscriptionInterval.Weekly => 7,
        SubscriptionInterval.BiWeekly => 14,
        SubscriptionInterval.Monthly => 30,
        SubscriptionInterval.Quarterly => 90,
        SubscriptionInterval.BiAnnually => 180,
        SubscriptionInterval.Annually => 365,
        _ => 30
    };

    private static DateTime ComputeNext(DateTime start, SubscriptionInterval i) => i switch
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

    private async Task<string> SendAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body, SerializeOptions);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Paytm failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Paytm {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private string ComputeChecksum(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.MerchantKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }

    private sealed class PaytmSubscriptionEnvelope<TBody>
    {
        [JsonPropertyName("body")] public TBody? Body { get; set; }
    }

    private sealed class PaytmSubscriptionBody
    {
        [JsonPropertyName("subscriptionId")] public string? SubscriptionId { get; set; }
        [JsonPropertyName("planId")] public string? PlanId { get; set; }
        [JsonPropertyName("customerId")] public string? CustomerId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("startDate")] public DateTime? StartDate { get; set; }
        [JsonPropertyName("nextBillingDate")] public DateTime? NextBillingDate { get; set; }
        [JsonPropertyName("cyclesCompleted")] public int? CyclesCompleted { get; set; }
        [JsonPropertyName("resultInfo")] public PaytmResultInfo? ResultInfo { get; set; }
    }

    private sealed class PaytmResultInfo
    {
        [JsonPropertyName("resultStatus")] public string? ResultStatus { get; set; }
        [JsonPropertyName("resultCode")] public string? ResultCode { get; set; }
        [JsonPropertyName("resultMsg")] public string? ResultMsg { get; set; }
    }
}
