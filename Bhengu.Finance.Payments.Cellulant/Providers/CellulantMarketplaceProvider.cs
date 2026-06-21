// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Cellulant.Configuration;
using Bhengu.Finance.Payments.Cellulant.Internals;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Marketplace;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Core.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Cellulant.Providers;

/// <summary>
/// Cellulant (Tingg) implementation of <see cref="IMarketplaceProvider"/>. Tingg models
/// marketplaces as beneficiaries on the merchant's service; split definitions are stored locally and
/// applied at charge time as the Tingg <c>charge_beneficiaries</c> array on the Express Checkout call
/// (verified: https://docs.tingg.africa/docs/checkout-v3-express-checkout).
/// </summary>
/// <remarks>
/// VERIFIED: the split mechanism on the express-request call (<c>charge_beneficiaries</c> with
/// <c>charge_beneficiary_code</c> + <c>amount</c>) and the host/auth (apiKey + Bearer).
/// UNVERIFIED: the programmatic sub-service onboarding endpoints (<c>services/v1/sub-services</c>) —
/// Tingg does not publicly document a self-serve beneficiary-onboarding API in Checkout 3.0; those
/// paths are retained from prior behaviour and flagged inline. See <c>CreateSubAccountAsync</c>.
/// </remarks>
/// <remarks>
/// <para>Tingg's onboarding is API-only at the sub-service tier; <see cref="SubAccount.OnboardingUrl"/>
/// is always null. The <see cref="SubAccountRequest.SettlementAccountToken"/> is required and is
/// either a bank account number or an MSISDN-keyed wallet, depending on the country.</para>
/// <para>Splits are persisted in the shared <see cref="IBhenguDistributedCache"/> for 7 days so
/// they survive single-process restarts when InMemoryBhenguDistributedCache is in use; in
/// production with Redis they survive arbitrarily.</para>
/// </remarks>
public sealed class CellulantMarketplaceProvider : BhenguProviderBase, IMarketplaceProvider
{
    private readonly HttpClient _httpClient;
    private readonly CellulantOptions _options;
    private readonly CellulantTokenBroker _tokenBroker;
    private readonly IBhenguDistributedCache _cache;
    private readonly ConcurrentDictionary<string, SplitDefinition> _splitCache = new();
    private static readonly TimeSpan s_splitTtl = TimeSpan.FromDays(7);

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Cellulant;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public CellulantMarketplaceProvider(
        HttpClient httpClient,
        IOptions<CellulantOptions> options,
        ILogger<CellulantMarketplaceProvider> logger,
        CellulantTokenBroker? tokenBroker = null,
        IBhenguDistributedCache? cache = null)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _tokenBroker = tokenBroker ?? new CellulantTokenBroker(options!, new Microsoft.Extensions.Logging.Abstractions.NullLogger<CellulantTokenBroker>());
        _cache = cache ?? new InMemoryBhenguDistributedCache();

        if (string.IsNullOrWhiteSpace(_options.ServiceCode))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(CellulantOptions.ServiceCode)} is required");

        if (_httpClient.BaseAddress is null)
        {
            // Verified Tingg Checkout 3.0 hosts. Source: https://docs.tingg.africa/reference/authenticate-requests
            var defaultUrl = _options.UseSandbox
                ? "https://api-approval.tingg.africa/"
                : "https://api.tingg.africa/";
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? defaultUrl);
        }
    }

    /// <inheritdoc/>
    public Task<SubAccount> CreateSubAccountAsync(SubAccountRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SettlementAccountToken))
            throw new BhenguPaymentException(ProviderName,
                "Cellulant sub-services require SettlementAccountToken (bank account / wallet MSISDN).",
                "missing_settlement_account");

        return RunOperationAsync("create_sub_account", async () =>
        {
            var cached = await TryGetCachedAsync<SubAccount>(request.IdempotencyKey, "sub_account", ct).ConfigureAwait(false);
            if (cached is not null) return cached;

            // UNVERIFIED: Tingg does not publicly document a self-serve sub-service / beneficiary
            // ONBOARDING API in Checkout 3.0 (beneficiary codes are provisioned via the merchant
            // portal / account manager, then referenced as `charge_beneficiaries.charge_beneficiary_code`
            // on the express-request call — which IS verified). The path + body below are retained from
            // prior behaviour and are NOT confirmed against Tingg docs. Do not rely on programmatic
            // sub-account creation in production until verified.
            var body = new
            {
                parentServiceCode = _options.ServiceCode,
                businessName = request.BusinessName,
                contactEmail = request.ContactEmail,
                countryCode = request.Country,
                settlementCurrency = request.SettlementCurrency ?? "KES",
                settlementAccount = request.SettlementAccountToken
            };

            var responseBody = await SendAuthorisedAsync(HttpMethod.Post, "services/v1/sub-services", body, ct, "CreateSubAccount").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<CellulantSubAccountResponse>(responseBody);
            if (response?.Data is null)
                throw new BhenguPaymentException(ProviderName, "Cellulant sub-service create returned no data", "no_subaccount_data");

            var sub = MapSubAccount(response.Data, request);
            await TrySetCachedAsync(request.IdempotencyKey, "sub_account", sub, ct).ConfigureAwait(false);
            return sub;
        }, ct);
    }

    /// <inheritdoc/>
    public Task<SubAccount?> GetSubAccountAsync(string subAccountReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subAccountReference);
        return RunOperationAsync("get_sub_account", async () =>
        {
            try
            {
                var body = await SendAuthorisedAsync(HttpMethod.Get, $"services/v1/sub-services/{Uri.EscapeDataString(subAccountReference)}", null, ct, "GetSubAccount").ConfigureAwait(false);
                var response = JsonSerializer.Deserialize<CellulantSubAccountResponse>(body);
                return response?.Data is { } data ? MapSubAccount(data, null) : null;
            }
            catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
            {
                return null;
            }
        }, ct);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SubAccount> ListSubAccountsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await RunOperationAsync("list_sub_accounts", async () =>
        {
            var body = await SendAuthorisedAsync(HttpMethod.Get, $"services/v1/sub-services?parentServiceCode={Uri.EscapeDataString(_options.ServiceCode)}", null, ct, "ListSubAccounts").ConfigureAwait(false);
            return JsonSerializer.Deserialize<CellulantSubAccountListResponse>(body);
        }, ct).ConfigureAwait(false);

        if (response?.Data is null) yield break;
        foreach (var d in response.Data)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapSubAccount(d, null);
        }
    }

    /// <inheritdoc/>
    public Task<SplitDefinition> CreateSplitAsync(SplitDefinitionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Rules.Count == 0)
            throw new BhenguPaymentException(ProviderName, "Split definition must contain at least one rule.", "empty_split");

        return RunOperationAsync("create_split", async () =>
        {
            var cached = await TryGetCachedAsync<SplitDefinition>(request.IdempotencyKey, "split", ct).ConfigureAwait(false);
            if (cached is not null) return cached;

            var reference = $"tingg-split-{Guid.NewGuid():N}";
            var split = new SplitDefinition
            {
                Reference = reference,
                Name = request.Name,
                Currency = request.Currency,
                Rules = request.Rules.ToArray()
            };

            _splitCache[reference] = split;
            await _cache.SetAsync(SplitCacheKey(reference), split, s_splitTtl, ct).ConfigureAwait(false);
            await TrySetCachedAsync(request.IdempotencyKey, "split", split, ct).ConfigureAwait(false);
            Logger.LogInformation("Cellulant split definition created: {Reference}", reference);
            return split;
        }, ct);
    }

    /// <inheritdoc/>
    public async Task<SplitDefinition?> GetSplitAsync(string splitReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(splitReference);
        if (_splitCache.TryGetValue(splitReference, out var local)) return local;
        var hit = await _cache.GetAsync<SplitDefinition>(SplitCacheKey(splitReference), ct).ConfigureAwait(false);
        if (hit is not null) _splitCache[splitReference] = hit;
        return hit;
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ChargeWithSplitAsync(ChargeWithSplitRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Payment.Currency, async () =>
        {
            IReadOnlyList<SplitRule> rules;
            if (request.InlineRules is { Count: > 0 })
                rules = request.InlineRules;
            else if (!string.IsNullOrEmpty(request.SplitReference))
            {
                var def = await GetSplitAsync(request.SplitReference, ct).ConfigureAwait(false)
                    ?? throw new BhenguPaymentException(ProviderName, $"Unknown split reference '{request.SplitReference}'.", "unknown_split");
                rules = def.Rules;
            }
            else
                throw new BhenguPaymentException(ProviderName, "ChargeWithSplitRequest requires SplitReference or InlineRules.", "missing_split");

            var email = request.Payment.Metadata?.GetValueOrDefault("email");
            var firstName = request.Payment.Metadata?.GetValueOrDefault("firstName")
                ?? request.Payment.Metadata?.GetValueOrDefault("name") ?? "Customer";
            var lastName = request.Payment.Metadata?.GetValueOrDefault("lastName") ?? "Customer";
            var msisdn = request.Payment.PaymentMethodToken;
            var merchantTxId = request.Payment.IdempotencyKey ?? $"tingg-split-{Guid.NewGuid():N}";

            // Tingg models splits as `charge_beneficiaries` on the express-request call, each entry a
            // { charge_beneficiary_code, amount } pair. Source:
            // https://docs.tingg.africa/docs/checkout-v3-express-checkout
            // Note: the documented split entry carries a FIXED `amount` only — percentage splits are
            // resolved client-side against the request amount before sending.
            var chargeBeneficiaries = rules.Select(r => new
            {
                charge_beneficiary_code = r.SubAccountReference,
                amount = r.ShareType == SplitShareType.FixedAmount
                    ? (r.Amount ?? 0m)
                    : Math.Round(request.Payment.Amount * (r.Percentage ?? 0m) / 100m, 2)
            }).ToArray();

            var accountNumber = BuildAccountNumber(merchantTxId);
            var body = new
            {
                customer_first_name = firstName,
                customer_last_name = lastName,
                customer_email = email,
                msisdn,
                account_number = accountNumber,
                request_amount = request.Payment.Amount.ToString(CultureInfo.InvariantCulture),
                merchant_transaction_id = merchantTxId,
                service_code = _options.ServiceCode,
                country_code = _options.CountryCode,
                currency_code = request.Payment.Currency.ToUpperInvariant(),
                request_description = request.Payment.Description,
                callback_url = _options.CallbackUrl,
                success_redirect_url = _options.CallbackUrl,
                fail_redirect_url = _options.CallbackUrl,
                charge_beneficiaries = chargeBeneficiaries
            };

            var responseBody = await SendAuthorisedAsync(HttpMethod.Post, "v3/checkout-api/checkout-request/express-request", body, ct, "ChargeWithSplit").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<CellulantCheckoutResponse>(responseBody);

            var statusCode = response?.Status?.StatusCode;
            return new PaymentResponse
            {
                GatewayReference = merchantTxId,
                Status = statusCode == 200 ? PaymentStatus.Pending : PaymentStatus.Failed,
                Amount = request.Payment.Amount,
                Currency = request.Payment.Currency,
                ProcessedAt = DateTime.UtcNow,
                RedirectUrl = response?.Results?.ShortUrl ?? response?.Results?.LongUrl,
                Message = response?.Status?.StatusDescription
            };
        }, ct);
    }

    private static SubAccount MapSubAccount(CellulantSubAccountData data, SubAccountRequest? fallback) => new()
    {
        Reference = data.SubServiceCode ?? string.Empty,
        BusinessName = data.BusinessName ?? fallback?.BusinessName ?? string.Empty,
        ContactEmail = data.ContactEmail ?? fallback?.ContactEmail,
        SettlementAccountToken = data.SettlementAccount ?? fallback?.SettlementAccountToken,
        IsActive = data.IsActive ?? true,
        OnboardingUrl = null
    };

    private async Task<string> SendAuthorisedAsync(HttpMethod method, string path, object? body, CancellationToken ct, string operation)
    {
        var token = await _tokenBroker.EnsureAccessTokenAsync(_httpClient, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Tingg requires the apiKey header on every call (case-insensitive on the wire).
        // Source: https://docs.tingg.africa/reference/authenticate-requests
        if (!string.IsNullOrEmpty(_options.ApiKey))
            req.Headers.TryAddWithoutValidation("apiKey", _options.ApiKey);

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Cellulant {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    /// <summary>
    /// Tingg's <c>account_number</c> must be ≤15 chars with no special chars (underscores allowed).
    /// Source: https://docs.tingg.africa/docs/checkout-v3-express-checkout
    /// </summary>
    private static string BuildAccountNumber(string merchantTransactionId)
    {
        var cleaned = new string(merchantTransactionId.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (cleaned.Length == 0) cleaned = Guid.NewGuid().ToString("N");
        return cleaned.Length <= 15 ? cleaned : cleaned[..15];
    }

    private async Task<T?> TryGetCachedAsync<T>(string? idempotencyKey, string operation, CancellationToken ct) where T : class
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return null;
        return await _cache.GetAsync<T>(IdemCacheKey(idempotencyKey, operation), ct).ConfigureAwait(false);
    }

    private async Task TrySetCachedAsync<T>(string? idempotencyKey, string operation, T value, CancellationToken ct) where T : class
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return;
        await _cache.SetAsync(IdemCacheKey(idempotencyKey, operation), value, TimeSpan.FromHours(24), ct).ConfigureAwait(false);
    }

    private static string IdemCacheKey(string idempotencyKey, string operation)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey))).ToLowerInvariant();
        return $"cellulant:idem:{operation}:{hash}";
    }

    private static string SplitCacheKey(string reference) => $"cellulant:split:{reference}";

    private sealed class CellulantSubAccountResponse
    {
        [JsonPropertyName("data")] public CellulantSubAccountData? Data { get; set; }
    }

    private sealed class CellulantSubAccountListResponse
    {
        [JsonPropertyName("data")] public List<CellulantSubAccountData>? Data { get; set; }
    }

    private sealed class CellulantSubAccountData
    {
        [JsonPropertyName("subServiceCode")] public string? SubServiceCode { get; set; }
        [JsonPropertyName("businessName")] public string? BusinessName { get; set; }
        [JsonPropertyName("contactEmail")] public string? ContactEmail { get; set; }
        [JsonPropertyName("settlementAccount")] public string? SettlementAccount { get; set; }
        [JsonPropertyName("isActive")] public bool? IsActive { get; set; }
    }

    // Verified express-request response envelope.
    // Source: https://docs.tingg.africa/docs/checkout-v3-express-checkout
    private sealed class CellulantCheckoutResponse
    {
        [JsonPropertyName("status")] public CellulantStatusEnvelope? Status { get; set; }
        [JsonPropertyName("results")] public CellulantCheckoutResults? Results { get; set; }
    }

    private sealed class CellulantStatusEnvelope
    {
        [JsonPropertyName("status_code")] public int? StatusCode { get; set; }
        [JsonPropertyName("status_description")] public string? StatusDescription { get; set; }
    }

    private sealed class CellulantCheckoutResults
    {
        [JsonPropertyName("short_url")] public string? ShortUrl { get; set; }
        [JsonPropertyName("long_url")] public string? LongUrl { get; set; }
    }
}
