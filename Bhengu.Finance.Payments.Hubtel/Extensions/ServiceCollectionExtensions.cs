// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Hubtel.Configuration;
using Bhengu.Finance.Payments.Hubtel.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Hubtel.Extensions;

/// <summary>DI registration helpers for the Hubtel provider family.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Hubtel provider. Reads configuration from <c>Bhengu:Finance:Payments:Hubtel</c>.
    /// Fails fast at startup if required options (ClientId, ClientSecret, MerchantAccountNumber) are missing.
    /// </summary>
    public static IServiceCollection AddHubtelPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(HubtelOptions.ConfigSection);
        services.Configure<HubtelOptions>(section);

        var probe = section.Get<HubtelOptions>() ?? new HubtelOptions();
        if (string.IsNullOrWhiteSpace(probe.ClientId))
            throw new ProviderConfigurationException("hubtel", $"{HubtelOptions.ConfigSection}:ClientId is required");
        if (string.IsNullOrWhiteSpace(probe.ClientSecret))
            throw new ProviderConfigurationException("hubtel", $"{HubtelOptions.ConfigSection}:ClientSecret is required");
        if (string.IsNullOrWhiteSpace(probe.MerchantAccountNumber))
            throw new ProviderConfigurationException("hubtel", $"{HubtelOptions.ConfigSection}:MerchantAccountNumber is required");

        services.AddBhenguInMemoryCache();

        services.AddHttpClient<HubtelPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, HubtelPaymentProvider>(sp =>
            sp.GetRequiredService<HubtelPaymentProvider>());
        services.AddTransient<IPayoutProvider, HubtelPaymentProvider>(sp =>
            sp.GetRequiredService<HubtelPaymentProvider>());
        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Hubtel, (sp, _) => sp.GetRequiredService<HubtelPaymentProvider>());
        services.AddKeyedTransient<IPayoutProvider>(ProviderNames.Hubtel, (sp, _) => sp.GetRequiredService<HubtelPaymentProvider>());

        services.AddHttpClient<HubtelSettlementProvider>();
        services.AddTransient<ISettlementProvider, HubtelSettlementProvider>(sp =>
            sp.GetRequiredService<HubtelSettlementProvider>());
        services.AddKeyedTransient<ISettlementProvider>(ProviderNames.Hubtel, (sp, _) => sp.GetRequiredService<HubtelSettlementProvider>());

        services.AddBhenguPaymentStartupValidation();
        return services;
    }
}
