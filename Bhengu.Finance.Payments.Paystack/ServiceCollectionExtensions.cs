// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Paystack.Internals;
using Bhengu.Finance.Payments.Paystack.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Paystack;

/// <summary>
/// Top-level Paystack DI registration. Registers every Paystack provider against its respective
/// Bhengu Core interface plus a keyed registration on <see cref="ProviderNames.Paystack"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register every Paystack provider (charge, payout, refund, tokenisation, subscriptions,
    /// disputes, settlements, marketplace) against its interface contract, plus a keyed
    /// <see cref="IPaymentGatewayProvider"/> registration under <see cref="ProviderNames.Paystack"/>.
    /// </summary>
    /// <remarks>
    /// <para>Requires <see cref="Configuration.PaystackOptions"/> to be configured separately —
    /// callers should pair this with <c>services.Configure&lt;PaystackOptions&gt;(...)</c> or use
    /// <see cref="Extensions.ServiceCollectionExtensions.AddPaystackPayments"/> which reads from
    /// <see cref="Configuration.PaystackOptions.ConfigSection"/> directly.</para>
    /// <para>The shared <see cref="PaystackIdempotencyCache"/> is registered as a singleton so all
    /// providers coalesce keys against the same in-memory store.</para>
    /// </remarks>
    public static IServiceCollection AddBhenguPaystackPayments(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<PaystackIdempotencyCache>();

        services.AddHttpClient<PaystackPaymentProvider>();
        services.AddHttpClient<PaystackTokenisationProvider>();
        services.AddHttpClient<PaystackSubscriptionProvider>();
        services.AddHttpClient<PaystackDisputeProvider>();
        services.AddHttpClient<PaystackSettlementProvider>();
        services.AddHttpClient<PaystackMarketplaceProvider>();
        services.AddHttpClient<PaystackPayoutProvider>();

        services.AddTransient<IPaymentGatewayProvider, PaystackPaymentProvider>(sp =>
            sp.GetRequiredService<PaystackPaymentProvider>());
        services.AddTransient<ITokenisationProvider, PaystackTokenisationProvider>(sp =>
            sp.GetRequiredService<PaystackTokenisationProvider>());
        services.AddTransient<ISubscriptionProvider, PaystackSubscriptionProvider>(sp =>
            sp.GetRequiredService<PaystackSubscriptionProvider>());
        services.AddTransient<IDisputeProvider, PaystackDisputeProvider>(sp =>
            sp.GetRequiredService<PaystackDisputeProvider>());
        services.AddTransient<ISettlementProvider, PaystackSettlementProvider>(sp =>
            sp.GetRequiredService<PaystackSettlementProvider>());
        services.AddTransient<IMarketplaceProvider, PaystackMarketplaceProvider>(sp =>
            sp.GetRequiredService<PaystackMarketplaceProvider>());
        services.AddTransient<IPayoutProvider, PaystackPayoutProvider>(sp =>
            sp.GetRequiredService<PaystackPayoutProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Paystack,
            (sp, _) => sp.GetRequiredService<PaystackPaymentProvider>());
        services.AddKeyedTransient<ITokenisationProvider>(ProviderNames.Paystack,
            (sp, _) => sp.GetRequiredService<PaystackTokenisationProvider>());
        services.AddKeyedTransient<ISubscriptionProvider>(ProviderNames.Paystack,
            (sp, _) => sp.GetRequiredService<PaystackSubscriptionProvider>());
        services.AddKeyedTransient<IDisputeProvider>(ProviderNames.Paystack,
            (sp, _) => sp.GetRequiredService<PaystackDisputeProvider>());
        services.AddKeyedTransient<ISettlementProvider>(ProviderNames.Paystack,
            (sp, _) => sp.GetRequiredService<PaystackSettlementProvider>());
        services.AddKeyedTransient<IMarketplaceProvider>(ProviderNames.Paystack,
            (sp, _) => sp.GetRequiredService<PaystackMarketplaceProvider>());
        services.AddKeyedTransient<IPayoutProvider>(ProviderNames.Paystack,
            (sp, _) => sp.GetRequiredService<PaystackPayoutProvider>());

        return services;
    }
}
