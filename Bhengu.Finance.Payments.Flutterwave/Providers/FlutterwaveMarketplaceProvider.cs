// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Marketplace;
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Bhengu.Finance.Payments.Flutterwave.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Flutterwave.Providers;

/// <summary>
/// Flutterwave marketplace provider — wraps <c>/v3/subaccounts</c> for sub-merchant onboarding and the
/// <c>subaccounts</c> parameter on <c>/v3/payments</c> for split charges.
/// <para>
/// Flutterwave has no first-class "split definition" object — splits are described inline on each
/// charge. To honour the <see cref="ISplitDefinition"/>-style contract we cache split definitions
/// in-process and resolve them on charge. Callers that need durable splits should persist the
/// <see cref="SplitDefinition"/> returned by <see cref="CreateSplitAsync"/> alongside their order.
/// </para>
/// </summary>
public sealed class FlutterwaveMarketplaceProvider : IMarketplaceProvider
{
    private const string SplitKeyPrefix = "flutterwave:split:";
    private static readonly TimeSpan SplitTtl = TimeSpan.FromDays(365);

    private readonly HttpClient _httpClient;
    private readonly FlutterwaveOptions _options;
    private readonly ILogger<FlutterwaveMarketplaceProvider> _logger;
    private readonly FlutterwaveIdempotencyCache _idempotencyCache;
    private readonly IBhenguDistributedCache _cache;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Flutterwave;

    /// <summary>Construct the provider; configures Bearer auth on the injected <paramref name="httpClient"/>.</summary>
    public FlutterwaveMarketplaceProvider(
        HttpClient httpClient,
        IOptions<FlutterwaveOptions> options,
        ILogger<FlutterwaveMarketplaceProvider> logger,
        IBhenguDistributedCache cache)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _idempotencyCache = new FlutterwaveIdempotencyCache(cache);

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(FlutterwaveOptions.SecretKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.flutterwave.com/");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
    }

    /// <summary>Back-compat constructor that uses the process-local in-memory cache.</summary>
    public FlutterwaveMarketplaceProvider(
        HttpClient httpClient,
        IOptions<FlutterwaveOptions> options,
        ILogger<FlutterwaveMarketplaceProvider> logger)
        : this(httpClient, options, logger, new InMemoryBhenguDistributedCache())
    {
    }

    /// <inheritdoc/>
    public Task<SubAccount> CreateSubAccountAsync(SubAccountRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return FlutterwaveObservability.ObserveAsync("create_sub_account", () =>
            _idempotencyCache.GetOrAddAsync(request.IdempotencyKey, () => CreateSubAccountCoreAsync(request, ct)));
    }

    private async Task<SubAccount> CreateSubAccountCoreAsync(SubAccountRequest request, CancellationToken ct)
    {
        // Flutterwave subaccount requires bank code + account number; we destructure SettlementAccountToken
        // as "bankCode:accountNumber" (mirrors FlutterwavePaymentProvider's PayoutRequest convention).
        if (string.IsNullOrWhiteSpace(request.SettlementAccountToken))
            throw new PaymentDeclinedException(ProviderName, "missing_destination",
                "Flutterwave subaccount requires SettlementAccountToken as 'bankCode:accountNumber'.");

        var colon = request.SettlementAccountToken.IndexOf(':');
        if (colon <= 0)
            throw new PaymentDeclinedException(ProviderName, "invalid_destination",
                "Flutterwave subaccount SettlementAccountToken must be 'bankCode:accountNumber'.");

        var bankCode = request.SettlementAccountToken[..colon];
        var accountNumber = request.SettlementAccountToken[(colon + 1)..];

        var body = new
        {
            account_bank = bankCode,
            account_number = accountNumber,
            business_name = request.BusinessName,
            business_email = request.ContactEmail,
            business_contact_mobile = request.Metadata?.GetValueOrDefault("business_contact_mobile") ?? string.Empty,
            business_contact = request.Metadata?.GetValueOrDefault("business_contact") ?? request.BusinessName,
            country = request.Country.ToUpperInvariant(),
            split_value = 0.0,
            split_type = "percentage"
        };

        var responseBody = await SendAsync(HttpMethod.Post, "v3/subaccounts", body, ct, "CreateSubAccount").ConfigureAwait(false);
        var fw = JsonSerializer.Deserialize<FlutterwaveSubAccountResponse>(responseBody);
        if (fw?.Data is null)
            throw new BhenguPaymentException(ProviderName, "Flutterwave CreateSubAccount returned no data");

        _logger.LogInformation("Flutterwave subaccount created: {Reference} business={Business}",
            fw.Data.SubAccountId, fw.Data.BusinessName);

        return ToSubAccount(fw.Data);
    }

    /// <inheritdoc/>
    public Task<SubAccount?> GetSubAccountAsync(string subAccountReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subAccountReference);
        return FlutterwaveObservability.ObserveAsync("get_sub_account", () => GetSubAccountCoreAsync(subAccountReference, ct));
    }

    private async Task<SubAccount?> GetSubAccountCoreAsync(string subAccountReference, CancellationToken ct)
    {
        try
        {
            var responseBody = await SendAsync(HttpMethod.Get, $"v3/subaccounts/{Uri.EscapeDataString(subAccountReference)}", body: null, ct, "GetSubAccount").ConfigureAwait(false);
            var fw = JsonSerializer.Deserialize<FlutterwaveSubAccountResponse>(responseBody);
            return fw?.Data is null ? null : ToSubAccount(fw.Data);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SubAccount> ListSubAccountsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var responseBody = await SendAsync(HttpMethod.Get, "v3/subaccounts", body: null, ct, "ListSubAccounts").ConfigureAwait(false);
        var fw = JsonSerializer.Deserialize<FlutterwaveSubAccountListResponse>(responseBody);
        if (fw?.Data is null) yield break;

        foreach (var d in fw.Data)
        {
            ct.ThrowIfCancellationRequested();
            yield return ToSubAccount(d);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Flutterwave has no server-side split-definition object. The created <see cref="SplitDefinition"/>
    /// is cached via <see cref="IBhenguDistributedCache"/> (365d TTL) and replayed on subsequent
    /// <see cref="ChargeWithSplitAsync"/> calls. Survives restarts when Redis is wired.
    /// </remarks>
    public Task<SplitDefinition> CreateSplitAsync(SplitDefinitionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return FlutterwaveObservability.ObserveAsync("create_split", () => CreateSplitCoreAsync(request, ct));
    }

    private async Task<SplitDefinition> CreateSplitCoreAsync(SplitDefinitionRequest request, CancellationToken ct)
    {
        var reference = $"split-{Guid.NewGuid():N}";
        var def = new SplitDefinition
        {
            Reference = reference,
            Name = request.Name,
            Currency = request.Currency.ToUpperInvariant(),
            Rules = request.Rules
        };
        await _cache.SetAsync(SplitKeyPrefix + reference, def, SplitTtl, ct).ConfigureAwait(false);
        _logger.LogInformation("Flutterwave split cached: {Reference} rules={Count}", reference, request.Rules.Count);
        return def;
    }

    /// <inheritdoc/>
    public Task<SplitDefinition?> GetSplitAsync(string splitReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(splitReference);
        return FlutterwaveObservability.ObserveAsync("get_split", () =>
            _cache.GetAsync<SplitDefinition>(SplitKeyPrefix + splitReference, ct));
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ChargeWithSplitAsync(ChargeWithSplitRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Payment);

        if (request.SplitReference is null && request.InlineRules is null)
            throw new BhenguPaymentException(ProviderName, "ChargeWithSplitRequest requires either SplitReference or InlineRules.");

        var idempotencyKey = request.Payment.IdempotencyKey;
        return FlutterwaveObservability.ObserveChargeAsync(request.Payment.Currency, () =>
            _idempotencyCache.GetOrAddAsync(idempotencyKey, () => ChargeWithSplitCoreAsync(request, ct)));
    }

    private async Task<PaymentResponse> ChargeWithSplitCoreAsync(ChargeWithSplitRequest request, CancellationToken ct)
    {
        var payment = request.Payment;
        var email = payment.Metadata?.GetValueOrDefault("email")
            ?? throw new PaymentDeclinedException(ProviderName, "missing_email",
                "Flutterwave requires an 'email' in PaymentRequest.Metadata.");

        IReadOnlyList<SplitRule> rules;
        if (request.SplitReference is not null)
        {
            var cached = await _cache.GetAsync<SplitDefinition>(SplitKeyPrefix + request.SplitReference, ct).ConfigureAwait(false)
                ?? throw new BhenguPaymentException(ProviderName, $"Unknown SplitReference '{request.SplitReference}'.");
            rules = cached.Rules;
        }
        else
        {
            rules = request.InlineRules!;
        }

        var subaccounts = new List<object>(rules.Count);
        foreach (var r in rules)
        {
            subaccounts.Add(new
            {
                id = r.SubAccountReference,
                transaction_split_ratio = r.ShareType == SplitShareType.Percentage ? r.Percentage : null,
                transaction_charge_type = r.ShareType == SplitShareType.FixedAmount ? "flat" : "percentage",
                transaction_charge = r.ShareType == SplitShareType.FixedAmount ? r.Amount : r.Percentage
            });
        }

        var body = new
        {
            tx_ref = payment.PaymentMethodToken,
            amount = payment.Amount.ToString("F2", CultureInfo.InvariantCulture),
            currency = payment.Currency.ToUpperInvariant(),
            redirect_url = _options.RedirectUrl,
            customer = new { email },
            customizations = new { title = payment.Description },
            subaccounts
        };

        var responseBody = await SendAsync(HttpMethod.Post, "v3/payments", body, ct, "ChargeWithSplit").ConfigureAwait(false);
        var fw = JsonSerializer.Deserialize<FlutterwavePaymentResponse>(responseBody);

        _logger.LogInformation("Flutterwave split charge initialised: {TxRef} status={Status} subaccounts={Count}",
            payment.PaymentMethodToken, fw?.Status, rules.Count);

        return new PaymentResponse
        {
            GatewayReference = payment.PaymentMethodToken,
            Status = MapStatus(fw?.Status ?? "pending"),
            Amount = payment.Amount,
            Currency = payment.Currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = fw?.Data?.Link,
            Message = fw?.Message
        };
    }

    private static SubAccount ToSubAccount(FlutterwaveSubAccountData d) => new()
    {
        Reference = d.SubAccountId ?? d.Id.ToString(CultureInfo.InvariantCulture),
        BusinessName = d.BusinessName ?? string.Empty,
        ContactEmail = d.BusinessEmail,
        SettlementAccountToken = string.IsNullOrEmpty(d.AccountNumber) ? null : $"{d.AccountBank}:{d.AccountNumber}",
        IsActive = string.Equals(d.MetaStatus, "active", StringComparison.OrdinalIgnoreCase) || d.Active,
        OnboardingUrl = null
    };

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "success" or "successful" or "completed" or "new" => PaymentStatus.Completed,
        "pending" or "processing" or "initialised" => PaymentStatus.Pending,
        "failed" or "abandoned" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    private async Task<string> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string operation)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Flutterwave failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Flutterwave {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    // === Flutterwave response shapes (internal) ===

    private sealed class FlutterwaveSubAccountResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public FlutterwaveSubAccountData? Data { get; set; }
    }

    private sealed class FlutterwaveSubAccountListResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("data")] public List<FlutterwaveSubAccountData>? Data { get; set; }
    }

    private sealed class FlutterwaveSubAccountData
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("subaccount_id")] public string? SubAccountId { get; set; }
        [JsonPropertyName("business_name")] public string? BusinessName { get; set; }
        [JsonPropertyName("business_email")] public string? BusinessEmail { get; set; }
        [JsonPropertyName("account_bank")] public string? AccountBank { get; set; }
        [JsonPropertyName("account_number")] public string? AccountNumber { get; set; }
        [JsonPropertyName("status")] public string? MetaStatus { get; set; }
        [JsonPropertyName("active")] public bool Active { get; set; }
    }

    private sealed class FlutterwavePaymentResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public FlutterwavePaymentData? Data { get; set; }
    }

    private sealed class FlutterwavePaymentData
    {
        [JsonPropertyName("link")] public string? Link { get; set; }
    }
}
