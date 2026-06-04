// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Bhengu.Finance.Payments.Flutterwave.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Flutterwave.Extensions;

/// <summary>
/// DI registration helpers for the Flutterwave provider family.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the core Flutterwave payment provider (charge / refund / payout / webhook).
    /// Reads configuration from <c>Bhengu:Finance:Payments:Flutterwave</c>.
    /// Fails fast at startup if required options (SecretKey) are missing.
    /// </summary>
    public static IServiceCollection AddFlutterwavePayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(FlutterwaveOptions.ConfigSection);
        services.Configure<FlutterwaveOptions>(section);

        var probe = section.Get<FlutterwaveOptions>() ?? new FlutterwaveOptions();
        if (string.IsNullOrWhiteSpace(probe.SecretKey))
            throw new ProviderConfigurationException("flutterwave", $"{FlutterwaveOptions.ConfigSection}:SecretKey is required");

        services.AddBhenguInMemoryCache();
        services.AddHttpClient<FlutterwavePaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, FlutterwavePaymentProvider>(sp =>
            sp.GetRequiredService<FlutterwavePaymentProvider>());
        services.AddTransient<IPayoutProvider, FlutterwavePaymentProvider>(sp =>
            sp.GetRequiredService<FlutterwavePaymentProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Flutterwave, (sp, _) => sp.GetRequiredService<FlutterwavePaymentProvider>());
        services.AddBhenguPaymentStartupValidation();

        return services;
    }

    /// <summary>
    /// Register the full Flutterwave provider family — payment / payout / tokenisation /
    /// subscription / settlement / marketplace — plus keyed registrations under
    /// <see cref="ProviderNames.Flutterwave"/> for every optional contract Flutterwave supports.
    /// <para>
    /// Reads configuration from <c>Bhengu:Finance:Payments:Flutterwave</c>. Fails fast at startup
    /// if <see cref="FlutterwaveOptions.SecretKey"/> is missing.
    /// </para>
    /// </summary>
    public static IServiceCollection AddBhenguFlutterwavePayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Reuse the core registration so the keyed payment/payout providers are wired up.
        services.AddFlutterwavePayments(configuration);

        services.AddHttpClient<FlutterwaveTokenisationProvider>();
        services.AddHttpClient<FlutterwaveRawCardTokenisationProvider>();
        services.AddHttpClient<FlutterwaveSubscriptionProvider>();
        services.AddHttpClient<FlutterwaveSettlementProvider>();
        services.AddHttpClient<FlutterwaveMarketplaceProvider>();
        services.AddHttpClient<FlutterwaveDisputeProvider>();

        services.AddTransient<ITokenisationProvider>(sp => sp.GetRequiredService<FlutterwaveTokenisationProvider>());
        services.AddTransient<IRawCardTokenisationProvider>(sp => sp.GetRequiredService<FlutterwaveRawCardTokenisationProvider>());
        services.AddTransient<ISubscriptionProvider>(sp => sp.GetRequiredService<FlutterwaveSubscriptionProvider>());
        services.AddTransient<ISettlementProvider>(sp => sp.GetRequiredService<FlutterwaveSettlementProvider>());
        services.AddTransient<IMarketplaceProvider>(sp => sp.GetRequiredService<FlutterwaveMarketplaceProvider>());
        services.AddTransient<IDisputeProvider>(sp => sp.GetRequiredService<FlutterwaveDisputeProvider>());

        services.AddKeyedTransient<ITokenisationProvider>(ProviderNames.Flutterwave, (sp, _) => sp.GetRequiredService<FlutterwaveTokenisationProvider>());
        services.AddKeyedTransient<IRawCardTokenisationProvider>(ProviderNames.Flutterwave, (sp, _) => sp.GetRequiredService<FlutterwaveRawCardTokenisationProvider>());
        services.AddKeyedTransient<ISubscriptionProvider>(ProviderNames.Flutterwave, (sp, _) => sp.GetRequiredService<FlutterwaveSubscriptionProvider>());
        services.AddKeyedTransient<ISettlementProvider>(ProviderNames.Flutterwave, (sp, _) => sp.GetRequiredService<FlutterwaveSettlementProvider>());
        services.AddKeyedTransient<IMarketplaceProvider>(ProviderNames.Flutterwave, (sp, _) => sp.GetRequiredService<FlutterwaveMarketplaceProvider>());
        services.AddKeyedTransient<IDisputeProvider>(ProviderNames.Flutterwave, (sp, _) => sp.GetRequiredService<FlutterwaveDisputeProvider>());
        services.AddKeyedTransient<IPayoutProvider>(ProviderNames.Flutterwave, (sp, _) => sp.GetRequiredService<FlutterwavePaymentProvider>());

        return services;
    }
}
