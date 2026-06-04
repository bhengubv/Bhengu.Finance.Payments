// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.ExpressPay.Configuration;
using Bhengu.Finance.Payments.ExpressPay.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.ExpressPay.Extensions;

/// <summary>DI registration helpers for the ExpressPay provider family.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the ExpressPay provider. Reads configuration from <c>Bhengu:Finance:Payments:ExpressPay</c>.
    /// Fails fast at startup if required options (MerchantId, ApiKey) are missing.
    /// </summary>
    public static IServiceCollection AddExpressPayPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ExpressPayOptions.ConfigSection);
        services.Configure<ExpressPayOptions>(section);

        var probe = section.Get<ExpressPayOptions>() ?? new ExpressPayOptions();
        if (string.IsNullOrWhiteSpace(probe.MerchantId))
            throw new ProviderConfigurationException("expresspay", $"{ExpressPayOptions.ConfigSection}:MerchantId is required");
        if (string.IsNullOrWhiteSpace(probe.ApiKey))
            throw new ProviderConfigurationException("expresspay", $"{ExpressPayOptions.ConfigSection}:ApiKey is required");

        services.AddBhenguInMemoryCache();

        services.AddHttpClient<ExpressPayPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, ExpressPayPaymentProvider>(sp =>
            sp.GetRequiredService<ExpressPayPaymentProvider>());
        services.AddTransient<IPayoutProvider, ExpressPayPaymentProvider>(sp =>
            sp.GetRequiredService<ExpressPayPaymentProvider>());
        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.ExpressPay, (sp, _) => sp.GetRequiredService<ExpressPayPaymentProvider>());
        services.AddKeyedTransient<IPayoutProvider>(ProviderNames.ExpressPay, (sp, _) => sp.GetRequiredService<ExpressPayPaymentProvider>());

        services.AddHttpClient<ExpressPaySettlementProvider>();
        services.AddTransient<ISettlementProvider, ExpressPaySettlementProvider>(sp =>
            sp.GetRequiredService<ExpressPaySettlementProvider>());
        services.AddKeyedTransient<ISettlementProvider>(ProviderNames.ExpressPay, (sp, _) => sp.GetRequiredService<ExpressPaySettlementProvider>());

        services.AddBhenguPaymentStartupValidation();
        return services;
    }
}
