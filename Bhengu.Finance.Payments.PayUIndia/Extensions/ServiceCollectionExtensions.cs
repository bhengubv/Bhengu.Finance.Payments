// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.PayUIndia.Configuration;
using Bhengu.Finance.Payments.PayUIndia.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.PayUIndia.Extensions;

/// <summary>
/// DI registration helpers for the PayU India provider family.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the PayU India payment + payout providers. Reads configuration from
    /// <c>Bhengu:Finance:Payments:PayUIndia</c>. Fails fast at startup if required options
    /// (MerchantKey, Salt) are missing. Also registers the in-memory distributed cache used
    /// for client-side idempotency-key dedupe — install <c>Bhengu.Finance.Payments.Redis</c>
    /// and call <c>AddBhenguRedisCache</c> to substitute a multi-replica cache.
    /// </summary>
    public static IServiceCollection AddPayUIndiaPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(PayUIndiaOptions.ConfigSection);
        services.Configure<PayUIndiaOptions>(section);

        var probe = section.Get<PayUIndiaOptions>() ?? new PayUIndiaOptions();
        if (string.IsNullOrWhiteSpace(probe.MerchantKey))
            throw new ProviderConfigurationException("payuindia", $"{PayUIndiaOptions.ConfigSection}:MerchantKey is required");
        if (string.IsNullOrWhiteSpace(probe.Salt))
            throw new ProviderConfigurationException("payuindia", $"{PayUIndiaOptions.ConfigSection}:Salt is required");

        services.AddBhenguInMemoryCache();

        services.AddHttpClient<PayUIndiaPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, PayUIndiaPaymentProvider>(sp =>
            sp.GetRequiredService<PayUIndiaPaymentProvider>());
        services.AddTransient<IPayoutProvider, PayUIndiaPaymentProvider>(sp =>
            sp.GetRequiredService<PayUIndiaPaymentProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.PayUIndia, (sp, _) => sp.GetRequiredService<PayUIndiaPaymentProvider>());
        services.AddKeyedTransient<IPayoutProvider>(ProviderNames.PayUIndia, (sp, _) => sp.GetRequiredService<PayUIndiaPaymentProvider>());

        // Optional contracts.
        services.AddHttpClient<PayUIndiaThreeDSecureProvider>();
        services.AddHttpClient<PayUIndiaTokenisationProvider>();
        services.AddHttpClient<PayUIndiaSubscriptionProvider>();
        services.AddHttpClient<PayUIndiaSettlementProvider>();

        services.AddTransient<IThreeDSecureProvider>(sp => sp.GetRequiredService<PayUIndiaThreeDSecureProvider>());
        services.AddTransient<ITokenisationProvider>(sp => sp.GetRequiredService<PayUIndiaTokenisationProvider>());
        services.AddTransient<ISubscriptionProvider>(sp => sp.GetRequiredService<PayUIndiaSubscriptionProvider>());
        services.AddTransient<ISettlementProvider>(sp => sp.GetRequiredService<PayUIndiaSettlementProvider>());

        services.AddKeyedTransient<IThreeDSecureProvider>(ProviderNames.PayUIndia, (sp, _) => sp.GetRequiredService<PayUIndiaThreeDSecureProvider>());
        services.AddKeyedTransient<ITokenisationProvider>(ProviderNames.PayUIndia, (sp, _) => sp.GetRequiredService<PayUIndiaTokenisationProvider>());
        services.AddKeyedTransient<ISubscriptionProvider>(ProviderNames.PayUIndia, (sp, _) => sp.GetRequiredService<PayUIndiaSubscriptionProvider>());
        services.AddKeyedTransient<ISettlementProvider>(ProviderNames.PayUIndia, (sp, _) => sp.GetRequiredService<PayUIndiaSettlementProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
