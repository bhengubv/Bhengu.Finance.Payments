// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Internals;
using Bhengu.Finance.Payments.Paystack.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Paystack.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Paystack provider family. Reads configuration from
    /// <c>Bhengu:Finance:Payments:Paystack</c> and fails fast at startup if required options
    /// (SecretKey) are missing. Registers charge + payout + refund + tokenisation + subscriptions
    /// + disputes + settlements + marketplace, each against its Core interface.
    /// </summary>
    public static IServiceCollection AddPaystackPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(PaystackOptions.ConfigSection);
        services.Configure<PaystackOptions>(section);

        var probe = section.Get<PaystackOptions>() ?? new PaystackOptions();
        if (string.IsNullOrWhiteSpace(probe.SecretKey))
            throw new ProviderConfigurationException("paystack", $"{PaystackOptions.ConfigSection}:SecretKey is required");

        services.AddBhenguInMemoryCache();
        services.AddSingleton<PaystackIdempotencyCache>();

        services.AddHttpClient<PaystackPaymentProvider>();
        services.AddHttpClient<PaystackTokenisationProvider>();
        services.AddHttpClient<PaystackRawCardTokenisationProvider>();
        services.AddHttpClient<PaystackSubscriptionProvider>();
        services.AddHttpClient<PaystackDisputeProvider>();
        services.AddHttpClient<PaystackSettlementProvider>();
        services.AddHttpClient<PaystackMarketplaceProvider>();
        services.AddHttpClient<PaystackPayoutProvider>();

        services.AddTransient<IPaymentGatewayProvider, PaystackPaymentProvider>(sp =>
            sp.GetRequiredService<PaystackPaymentProvider>());
        services.AddTransient<IPayoutProvider, PaystackPaymentProvider>(sp =>
            sp.GetRequiredService<PaystackPaymentProvider>());
        services.AddTransient<ITokenisationProvider, PaystackTokenisationProvider>(sp =>
            sp.GetRequiredService<PaystackTokenisationProvider>());
        services.AddTransient<IRawCardTokenisationProvider, PaystackRawCardTokenisationProvider>(sp =>
            sp.GetRequiredService<PaystackRawCardTokenisationProvider>());
        services.AddTransient<ISubscriptionProvider, PaystackSubscriptionProvider>(sp =>
            sp.GetRequiredService<PaystackSubscriptionProvider>());
        services.AddTransient<IDisputeProvider, PaystackDisputeProvider>(sp =>
            sp.GetRequiredService<PaystackDisputeProvider>());
        services.AddTransient<ISettlementProvider, PaystackSettlementProvider>(sp =>
            sp.GetRequiredService<PaystackSettlementProvider>());
        services.AddTransient<IMarketplaceProvider, PaystackMarketplaceProvider>(sp =>
            sp.GetRequiredService<PaystackMarketplaceProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Paystack,
            (sp, _) => sp.GetRequiredService<PaystackPaymentProvider>());
        services.AddKeyedTransient<ITokenisationProvider>(ProviderNames.Paystack,
            (sp, _) => sp.GetRequiredService<PaystackTokenisationProvider>());
        services.AddKeyedTransient<IRawCardTokenisationProvider>(ProviderNames.Paystack,
            (sp, _) => sp.GetRequiredService<PaystackRawCardTokenisationProvider>());
        services.AddKeyedTransient<ISubscriptionProvider>(ProviderNames.Paystack,
            (sp, _) => sp.GetRequiredService<PaystackSubscriptionProvider>());
        services.AddKeyedTransient<IDisputeProvider>(ProviderNames.Paystack,
            (sp, _) => sp.GetRequiredService<PaystackDisputeProvider>());
        services.AddKeyedTransient<ISettlementProvider>(ProviderNames.Paystack,
            (sp, _) => sp.GetRequiredService<PaystackSettlementProvider>());
        services.AddKeyedTransient<IMarketplaceProvider>(ProviderNames.Paystack,
            (sp, _) => sp.GetRequiredService<PaystackMarketplaceProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
