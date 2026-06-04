// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Yoco.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Yoco.Providers;

/// <summary>
/// Yoco implementation of <see cref="ITokenisationProvider"/> (READ-side).
/// </summary>
/// <remarks>
/// <para>Yoco's vault is opaque to merchants — there is no public GET-by-token endpoint and no
/// list-by-customer endpoint either. The SDK answers reads from an in-memory cache populated by
/// <see cref="YocoRawCardTokenisationProvider.TokeniseAsync"/> and/or by the merchant's own webhook
/// listener that persists card descriptors as they surface on <c>payment.succeeded</c> /
/// <c>checkout.succeeded</c> events.</para>
/// <para>Cross-process consistency requires a shared cache backend — wire up
/// <c>Bhengu.Finance.Payments.Redis</c> and Yoco's read methods will see what other replicas wrote.</para>
/// <para>The WRITE counterpart that takes raw card details is the separate
/// <see cref="YocoRawCardTokenisationProvider"/>. Splitting the read and write surface keeps
/// PCI-DSS scope explicit at the type-system level.</para>
/// </remarks>
public sealed class YocoTokenisationProvider : BhenguProviderBase, ITokenisationProvider
{
    private readonly HttpClient _httpClient;
    private readonly YocoOptions _options;
    private readonly YocoTokenCache _cache;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Yoco;

    /// <summary>Construct a Yoco tokenisation provider. Designed to be registered via DI.</summary>
    public YocoTokenisationProvider(
        HttpClient httpClient,
        IOptions<YocoOptions> options,
        ILogger<YocoTokenisationProvider> logger,
        YocoTokenCache cache)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(YocoOptions.SecretKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://online.yoco.com/v1/");

        if (_httpClient.DefaultRequestHeaders.Authorization is null)
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
    }

    /// <inheritdoc/>
    public Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return RunOperationAsync<PaymentMethod?>("get_payment_method", () =>
        {
            var cached = _cache.TryGet(token);
            if (cached is null)
            {
                Logger.LogInformation(
                    "Yoco has no GET-by-token endpoint; returning null for {Token}. Persist tokens on the webhook for cross-process lookups.",
                    token);
            }
            return Task.FromResult(cached);
        }, ct);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<PaymentMethod> ListPaymentMethodsAsync(
        string customerId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(customerId);
        ct.ThrowIfCancellationRequested();

        Logger.LogInformation(
            "Yoco does not list tokens per customer; returning empty stream for {CustomerId}. Persist a (customer → token) index in your own store.",
            customerId);

        // Yoco has no list endpoint. Yield nothing — but keep the method async so consumers can
        // `await foreach` without special-casing the empty stream.
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    /// <inheritdoc/>
    public Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return RunOperationAsync("delete_payment_method", () => DeletePaymentMethodCoreAsync(token, ct), ct);
    }

    private async Task<bool> DeletePaymentMethodCoreAsync(string token, CancellationToken ct)
    {
        // Yoco's documented merchant API does not expose a card-delete endpoint. We attempt the
        // optional `/cards/{token}` DELETE for tenants that have the beta endpoint enabled;
        // otherwise we evict the in-process cache and return true so callers can move on.
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"cards/{Uri.EscapeDataString(token)}");
            HttpResponseMessage resp;
            try
            {
                resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                Logger.LogInformation(ex, "Yoco DELETE /cards is unavailable; treating as client-side delete for {Token}", token);
                _cache.Remove(token);
                return true;
            }

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                _cache.Remove(token);
                return false;
            }

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Logger.LogInformation(
                    "Yoco DELETE /cards/{Token} returned {Status} — treating as client-side delete. Body={Body}",
                    token, resp.StatusCode, body);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Yoco DELETE /cards/{Token} raised; treating as client-side delete", token);
        }

        _cache.Remove(token);
        return true;
    }
}

/// <summary>
/// Distributed-cache-backed registry of Yoco card tokens captured at tokenisation / webhook time.
/// Yoco has no public GET-by-token endpoint, so the SDK records what it knows here and the
/// caller is responsible for refreshing the cache on each webhook delivery.
/// </summary>
/// <remarks>
/// Entries are written to <see cref="Bhengu.Finance.Payments.Core.Caching.IBhenguDistributedCache"/>
/// with a 365-day TTL so tokens survive process restarts and remain consistent across replicas
/// when Redis is wired up via the optional <c>Bhengu.Finance.Payments.Redis</c> package.
/// </remarks>
public sealed class YocoTokenCache
{
    private const string KeyPrefix = "yoco:token:";
    private static readonly TimeSpan TimeToLive = TimeSpan.FromDays(365);

    private readonly Bhengu.Finance.Payments.Core.Caching.IBhenguDistributedCache _cache;

    /// <summary>Construct with an injected distributed cache. Used in DI-driven scenarios.</summary>
    public YocoTokenCache(Bhengu.Finance.Payments.Core.Caching.IBhenguDistributedCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>Default-constructor convenience for tests and back-compat callers.</summary>
    public YocoTokenCache() : this(new Bhengu.Finance.Payments.Core.Caching.InMemoryBhenguDistributedCache()) { }

    /// <summary>Add or overwrite a payment-method entry keyed by its token.</summary>
    public void Set(PaymentMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        _cache.SetAsync(KeyPrefix + method.Token, method, TimeToLive).GetAwaiter().GetResult();
    }

    /// <summary>Retrieve a cached payment method, or <c>null</c> if not present.</summary>
    public PaymentMethod? TryGet(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return _cache.GetAsync<PaymentMethod>(KeyPrefix + token).GetAwaiter().GetResult();
    }

    /// <summary>Remove a payment-method entry. Returns true if the token existed previously.</summary>
    public bool Remove(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        var existed = _cache.GetAsync<PaymentMethod>(KeyPrefix + token).GetAwaiter().GetResult() is not null;
        _cache.RemoveAsync(KeyPrefix + token).GetAwaiter().GetResult();
        return existed;
    }
}
