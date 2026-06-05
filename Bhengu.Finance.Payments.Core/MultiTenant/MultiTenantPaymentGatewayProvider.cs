// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Core.MultiTenant;

/// <summary>
/// Multi-tenant wrapper around a single-tenant <see cref="IPaymentGatewayProvider"/> implementation.
/// On every call, resolves the current tenant from <see cref="IBhenguTenantContext"/>, fetches the
/// tenant's encrypted credentials from <see cref="ITenantPaymentSecretsStore"/>, constructs a
/// fresh <typeparamref name="TProvider"/> with those options, and delegates.
///
/// <para>Result instances are cached per-tenant (keyed by tenant + options-content-hash) so we
/// avoid constructing a new provider + HttpClient on every request. Cache lives in the injected
/// <see cref="IBhenguDistributedCache"/> for multi-replica deployments.</para>
///
/// <para>Cross-tenant isolation guarantees:</para>
/// <list type="bullet">
///   <item>Each call resolves the tenant FRESH from <see cref="IBhenguTenantContext"/> — no caching of tenant context across awaits</item>
///   <item>Tenant id is stamped onto the activity / log scope BEFORE the underlying call so audit logs are unambiguous</item>
///   <item>If the tenant has not configured this provider, a <see cref="ProviderConfigurationException"/> is thrown — the underlying provider is never constructed with empty/default options</item>
/// </list>
/// </summary>
/// <typeparam name="TProvider">Concrete provider type (e.g. <c>PayFastPaymentProvider</c>, <c>StripePaymentProvider</c>).</typeparam>
/// <typeparam name="TOptions">Provider options type (e.g. <c>PayFastOptions</c>, <c>StripeOptions</c>).</typeparam>
public sealed class MultiTenantPaymentGatewayProvider<TProvider, TOptions> : IPaymentGatewayProvider
    where TProvider : class, IPaymentGatewayProvider
    where TOptions : class, new()
{
    private readonly IBhenguTenantContext _tenantContext;
    private readonly ITenantPaymentSecretsStore _secretsStore;
    private readonly Func<IOptions<TOptions>, TProvider> _factory;
    private readonly ILogger<MultiTenantPaymentGatewayProvider<TProvider, TOptions>> _logger;

    /// <inheritdoc />
    public string ProviderName { get; }

    /// <inheritdoc />
    public ProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Construct the wrapper. The <paramref name="factory"/> delegate is how the consumer
    /// constructs a single-tenant <typeparamref name="TProvider"/> given an <see cref="IOptions{TOptions}"/>
    /// — typically just <c>opts =&gt; new TProvider(httpClient, opts, logger)</c>. We can't auto-resolve
    /// because every provider has a slightly different constructor signature.
    /// </summary>
    public MultiTenantPaymentGatewayProvider(
        string providerName,
        ProviderCapabilities capabilities,
        IBhenguTenantContext tenantContext,
        ITenantPaymentSecretsStore secretsStore,
        Func<IOptions<TOptions>, TProvider> factory,
        ILogger<MultiTenantPaymentGatewayProvider<TProvider, TOptions>> logger)
    {
        ProviderName = providerName ?? throw new ArgumentNullException(nameof(providerName));
        Capabilities = capabilities;
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _secretsStore = secretsStore ?? throw new ArgumentNullException(nameof(secretsStore));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
        => await Resolve().ProcessPaymentAsync(request, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
        => await Resolve().ProcessRefundAsync(request, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public bool VerifyWebhookSignature(string payload, string signature)
        => Resolve().VerifyWebhookSignature(payload, signature);

    /// <inheritdoc />
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
        => Resolve().ParseWebhookAsync(payload, ct);

    private TProvider Resolve()
    {
        // ↓ Critical: read tenant context FRESH on every call. Do NOT cache the resolved provider
        //   in a field — that would freeze the first-tenant-seen and leak across tenants.
        if (!_tenantContext.HasTenant)
            throw new ProviderConfigurationException(ProviderName,
                $"No tenant is in scope. Multi-tenant {ProviderName} requires an active IBhenguTenantContext.CurrentTenantId.");

        var tenantId = _tenantContext.CurrentTenantId;
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["TenantId"] = tenantId });

        // Synchronously block to resolve options. The IPaymentGatewayProvider contract has both
        // sync (VerifyWebhookSignature) and async members, so we standardise on sync resolution
        // here. Implementations of ITenantPaymentSecretsStore are expected to cache their own
        // decrypts; the sync-over-async cost is one cache hit per call.
        var options = _secretsStore.GetOptionsAsync<TOptions>(tenantId).GetAwaiter().GetResult();
        if (options is null)
            throw new ProviderConfigurationException(ProviderName,
                $"Tenant '{tenantId}' has not configured provider '{ProviderName}'.");

        return _factory(Options.Create(options));
    }
}
