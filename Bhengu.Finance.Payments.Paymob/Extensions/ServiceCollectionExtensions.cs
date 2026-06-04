// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Paymob.Configuration;
using Bhengu.Finance.Payments.Paymob.Internals;
using Bhengu.Finance.Payments.Paymob.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Paymob.Extensions;

/// <summary>
/// DI registration for the Paymob provider family — payment + payout + tokenisation +
/// subscriptions + 3DS + settlement, all keyed under <see cref="ProviderNames.Paymob"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the full Paymob provider family. Reads configuration from
    /// <c>Bhengu:Finance:Payments:Paymob</c>. Fails fast at startup if required options
    /// (<see cref="PaymobOptions.ApiKey"/>) are missing.
    /// </summary>
    public static IServiceCollection AddPaymobPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(PaymobOptions.ConfigSection);
        services.Configure<PaymobOptions>(section);

        var probe = section.Get<PaymobOptions>() ?? new PaymobOptions();
        if (string.IsNullOrWhiteSpace(probe.ApiKey))
            throw new ProviderConfigurationException("paymob", $"{PaymobOptions.ConfigSection}:ApiKey is required");

        services.AddBhenguInMemoryCache();
        services.AddSingleton<PaymobIdempotencyCache>();

        services.AddHttpClient<PaymobPaymentProvider>();
        services.AddHttpClient<PaymobTokenisationProvider>();
        services.AddHttpClient<PaymobRawCardTokenisationProvider>();
        services.AddHttpClient<PaymobSubscriptionProvider>();
        services.AddHttpClient<PaymobThreeDSecureProvider>();
        services.AddHttpClient<PaymobSettlementProvider>();

        services.AddTransient<IPaymentGatewayProvider, PaymobPaymentProvider>(sp =>
            sp.GetRequiredService<PaymobPaymentProvider>());
        services.AddTransient<IPayoutProvider, PaymobPaymentProvider>(sp =>
            sp.GetRequiredService<PaymobPaymentProvider>());
        services.AddTransient<ITokenisationProvider, PaymobTokenisationProvider>(sp =>
            sp.GetRequiredService<PaymobTokenisationProvider>());
        services.AddTransient<IRawCardTokenisationProvider, PaymobRawCardTokenisationProvider>(sp =>
            sp.GetRequiredService<PaymobRawCardTokenisationProvider>());
        services.AddTransient<ISubscriptionProvider, PaymobSubscriptionProvider>(sp =>
            sp.GetRequiredService<PaymobSubscriptionProvider>());
        services.AddTransient<IThreeDSecureProvider, PaymobThreeDSecureProvider>(sp =>
            sp.GetRequiredService<PaymobThreeDSecureProvider>());
        services.AddTransient<ISettlementProvider, PaymobSettlementProvider>(sp =>
            sp.GetRequiredService<PaymobSettlementProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Paymob,
            (sp, _) => sp.GetRequiredService<PaymobPaymentProvider>());
        services.AddKeyedTransient<IPayoutProvider>(ProviderNames.Paymob,
            (sp, _) => sp.GetRequiredService<PaymobPaymentProvider>());
        services.AddKeyedTransient<ITokenisationProvider>(ProviderNames.Paymob,
            (sp, _) => sp.GetRequiredService<PaymobTokenisationProvider>());
        services.AddKeyedTransient<IRawCardTokenisationProvider>(ProviderNames.Paymob,
            (sp, _) => sp.GetRequiredService<PaymobRawCardTokenisationProvider>());
        services.AddKeyedTransient<ISubscriptionProvider>(ProviderNames.Paymob,
            (sp, _) => sp.GetRequiredService<PaymobSubscriptionProvider>());
        services.AddKeyedTransient<IThreeDSecureProvider>(ProviderNames.Paymob,
            (sp, _) => sp.GetRequiredService<PaymobThreeDSecureProvider>());
        services.AddKeyedTransient<ISettlementProvider>(ProviderNames.Paymob,
            (sp, _) => sp.GetRequiredService<PaymobSettlementProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
