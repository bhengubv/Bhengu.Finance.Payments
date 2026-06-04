// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Stripe.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Stripe;

/// <summary>
/// Aggregate DI extension for the Stripe provider family. Registers every Stripe-backed contract
/// (charge, payout, tokenisation, subscription, 3DS, dispute, settlement, mandate, marketplace)
/// against both its unqualified interface and the keyed-services lookup (<see cref="ProviderNames.Stripe"/>).
/// </summary>
/// <remarks>
/// Call <c>AddStripePayments(IConfiguration)</c> (in <see cref="Extensions.ServiceCollectionExtensions"/>)
/// first to bind <see cref="Configuration.StripeOptions"/>; this method then layers the per-contract
/// providers on top. Both extensions are safe to call together.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register every Stripe provider against its interface and as a keyed service under
    /// <see cref="ProviderNames.Stripe"/>. Idempotent — safe to call multiple times.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddBhenguStripePayments(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Distributed cache backing the in-process split cache used by StripeMarketplaceProvider.
        services.AddBhenguInMemoryCache();

        // Each typed HttpClient gives us per-provider connection pooling and lets consumers
        // wire Polly / Logging handlers per-provider without crosstalk.
        services.AddHttpClient<StripePaymentProvider>();
        services.AddHttpClient<StripeTokenisationProvider>();
        services.AddHttpClient<StripeSubscriptionProvider>();
        services.AddHttpClient<StripeThreeDSecureProvider>();
        services.AddHttpClient<StripeDisputeProvider>();
        services.AddHttpClient<StripeSettlementProvider>();
        services.AddHttpClient<StripeMandateProvider>();
        services.AddHttpClient<StripeMarketplaceProvider>();

        // Interface mappings — consumers can resolve any optional contract via DI without
        // knowing it came from Stripe.
        services.AddTransient<IPaymentGatewayProvider, StripePaymentProvider>(sp => sp.GetRequiredService<StripePaymentProvider>());
        services.AddTransient<IPayoutProvider, StripePaymentProvider>(sp => sp.GetRequiredService<StripePaymentProvider>());
        services.AddTransient<ITokenisationProvider, StripeTokenisationProvider>(sp => sp.GetRequiredService<StripeTokenisationProvider>());
        services.AddTransient<ISubscriptionProvider, StripeSubscriptionProvider>(sp => sp.GetRequiredService<StripeSubscriptionProvider>());
        services.AddTransient<IThreeDSecureProvider, StripeThreeDSecureProvider>(sp => sp.GetRequiredService<StripeThreeDSecureProvider>());
        services.AddTransient<IDisputeProvider, StripeDisputeProvider>(sp => sp.GetRequiredService<StripeDisputeProvider>());
        services.AddTransient<ISettlementProvider, StripeSettlementProvider>(sp => sp.GetRequiredService<StripeSettlementProvider>());
        services.AddTransient<IMandateProvider, StripeMandateProvider>(sp => sp.GetRequiredService<StripeMandateProvider>());
        services.AddTransient<IMarketplaceProvider, StripeMarketplaceProvider>(sp => sp.GetRequiredService<StripeMarketplaceProvider>());

        // Keyed lookups — consumers can pick the Stripe variant explicitly:
        //   [FromKeyedServices(ProviderNames.Stripe)] ITokenisationProvider vault
        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Stripe, (sp, _) => sp.GetRequiredService<StripePaymentProvider>());
        services.AddKeyedTransient<IPayoutProvider>(ProviderNames.Stripe, (sp, _) => sp.GetRequiredService<StripePaymentProvider>());
        services.AddKeyedTransient<ITokenisationProvider>(ProviderNames.Stripe, (sp, _) => sp.GetRequiredService<StripeTokenisationProvider>());
        services.AddKeyedTransient<ISubscriptionProvider>(ProviderNames.Stripe, (sp, _) => sp.GetRequiredService<StripeSubscriptionProvider>());
        services.AddKeyedTransient<IThreeDSecureProvider>(ProviderNames.Stripe, (sp, _) => sp.GetRequiredService<StripeThreeDSecureProvider>());
        services.AddKeyedTransient<IDisputeProvider>(ProviderNames.Stripe, (sp, _) => sp.GetRequiredService<StripeDisputeProvider>());
        services.AddKeyedTransient<ISettlementProvider>(ProviderNames.Stripe, (sp, _) => sp.GetRequiredService<StripeSettlementProvider>());
        services.AddKeyedTransient<IMandateProvider>(ProviderNames.Stripe, (sp, _) => sp.GetRequiredService<StripeMandateProvider>());
        services.AddKeyedTransient<IMarketplaceProvider>(ProviderNames.Stripe, (sp, _) => sp.GetRequiredService<StripeMarketplaceProvider>());

        services.AddBhenguPaymentStartupValidation();
        return services;
    }
}
