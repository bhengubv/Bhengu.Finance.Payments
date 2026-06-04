// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.BricsPay.Configuration;
using Bhengu.Finance.Payments.BricsPay.Currency;
using Bhengu.Finance.Payments.BricsPay.Providers;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.BricsPay.Extensions;

/// <summary>DI registration helpers for the BRICS Pay provider family.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the BRICS Pay provider family (payment + payout + settlement) plus the supporting
    /// <see cref="ICurrencyExchangeService"/>. Reads configuration from
    /// <c>Bhengu:Finance:Payments:BricsPay</c>.
    /// </summary>
    public static IServiceCollection AddBricsPayPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(BricsPayOptions.ConfigSection);
        services.Configure<BricsPayOptions>(section);

        var probe = section.Get<BricsPayOptions>() ?? new BricsPayOptions();
        if (string.IsNullOrWhiteSpace(probe.MerchantId))
            throw new ProviderConfigurationException("bricspay", $"{BricsPayOptions.ConfigSection}:MerchantId is required");
        if (string.IsNullOrWhiteSpace(probe.SecretKey))
            throw new ProviderConfigurationException("bricspay", $"{BricsPayOptions.ConfigSection}:SecretKey is required");

        services.AddBhenguInMemoryCache();
        services.AddHttpClient<ICurrencyExchangeService, CurrencyExchangeService>();
        services.AddHttpClient<BricsPayPaymentProvider>();
        services.AddHttpClient<BricsPaySettlementProvider>();

        services.AddTransient<IPaymentGatewayProvider, BricsPayPaymentProvider>(sp =>
            sp.GetRequiredService<BricsPayPaymentProvider>());
        services.AddTransient<IPayoutProvider, BricsPayPaymentProvider>(sp =>
            sp.GetRequiredService<BricsPayPaymentProvider>());
        services.AddTransient<ISettlementProvider, BricsPaySettlementProvider>(sp =>
            sp.GetRequiredService<BricsPaySettlementProvider>());

        // Register as keyed service so consumers can resolve by name: [FromKeyedServices(ProviderNames.BricsPay)]
        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.BricsPay,
            (sp, _) => sp.GetRequiredService<BricsPayPaymentProvider>());
        services.AddKeyedTransient<IPayoutProvider>(ProviderNames.BricsPay,
            (sp, _) => sp.GetRequiredService<BricsPayPaymentProvider>());
        services.AddKeyedTransient<ISettlementProvider>(ProviderNames.BricsPay,
            (sp, _) => sp.GetRequiredService<BricsPaySettlementProvider>());

        // Eager startup validation — fails the app at startup if config is broken (vs first request)
        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
