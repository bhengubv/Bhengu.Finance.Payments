// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Core.MultiTenant;

/// <summary>
/// DI helpers for wiring up multi-tenant payment providers.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register a multi-tenant wrapper around a concrete payment provider. The wrapper resolves
    /// the active tenant from <see cref="IBhenguTenantContext"/> on every call and fetches its
    /// credentials from <see cref="ITenantPaymentSecretsStore"/>.
    ///
    /// <para>Consumers must also register:</para>
    /// <list type="bullet">
    ///   <item><see cref="IBhenguTenantContext"/> — their own implementation (usually backed by IHttpContextAccessor)</item>
    ///   <item><see cref="ITenantPaymentSecretsStore"/> — their own implementation (usually backed by their tenant DB)</item>
    /// </list>
    /// </summary>
    /// <typeparam name="TProvider">Concrete provider type (e.g. PayFastPaymentProvider, StripePaymentProvider).</typeparam>
    /// <typeparam name="TOptions">Provider options type (e.g. PayFastOptions, StripeOptions).</typeparam>
    /// <param name="services">The DI container.</param>
    /// <param name="providerName">Canonical provider name (see <see cref="ProviderNames"/>).</param>
    /// <param name="capabilities">Capabilities the wrapped provider exposes.</param>
    /// <param name="factory">Constructs the single-tenant TProvider from per-request options. Typically <c>opts =&gt; ActivatorUtilities.CreateInstance&lt;TProvider&gt;(sp, opts)</c>.</param>
    public static IServiceCollection AddBhenguMultiTenantProvider<TProvider, TOptions>(
        this IServiceCollection services,
        string providerName,
        ProviderCapabilities capabilities,
        Func<IServiceProvider, IOptions<TOptions>, TProvider> factory)
        where TProvider : class, IPaymentGatewayProvider
        where TOptions : class, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(providerName);
        ArgumentNullException.ThrowIfNull(factory);

        // The tenant-aware wrapper is registered scoped so that one wrapper instance per request
        // sees a stable IBhenguTenantContext for the duration of the call.
        services.AddScoped<IPaymentGatewayProvider>(sp => new MultiTenantPaymentGatewayProvider<TProvider, TOptions>(
            providerName,
            capabilities,
            sp.GetRequiredService<IBhenguTenantContext>(),
            sp.GetRequiredService<ITenantPaymentSecretsStore>(),
            opts => factory(sp, opts),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MultiTenantPaymentGatewayProvider<TProvider, TOptions>>>()));

        // Also register the keyed-services variant so consumers can resolve by name.
        services.AddKeyedScoped<IPaymentGatewayProvider>(providerName, (sp, _) =>
            new MultiTenantPaymentGatewayProvider<TProvider, TOptions>(
                providerName,
                capabilities,
                sp.GetRequiredService<IBhenguTenantContext>(),
                sp.GetRequiredService<ITenantPaymentSecretsStore>(),
                opts => factory(sp, opts),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MultiTenantPaymentGatewayProvider<TProvider, TOptions>>>()));

        return services;
    }

    /// <summary>
    /// Register the default <see cref="AesGcmPaymentSecretsCipher"/>. Pass the 32-byte symmetric
    /// key directly; production deployments should swap this for a KMS-backed cipher by
    /// registering their own <see cref="IPaymentSecretsCipher"/> AFTER this call.
    /// </summary>
    public static IServiceCollection AddBhenguAesGcmSecretsCipher(this IServiceCollection services, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(key);
        services.TryAddSingleton<IPaymentSecretsCipher>(new AesGcmPaymentSecretsCipher(key));
        return services;
    }
}
