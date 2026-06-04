// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.CMI.Configuration;
using Bhengu.Finance.Payments.CMI.Providers;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.CMI.Extensions;

/// <summary>DI registration for the CMI provider family — payment + 3DS + settlement.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the full CMI provider family. Reads configuration from
    /// <c>Bhengu:Finance:Payments:CMI</c>. Fails fast at startup if required options
    /// (<see cref="CMIOptions.ClientId"/>, <see cref="CMIOptions.StoreKey"/>) are missing.
    /// </summary>
    public static IServiceCollection AddCMIPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(CMIOptions.ConfigSection);
        services.Configure<CMIOptions>(section);

        var probe = section.Get<CMIOptions>() ?? new CMIOptions();
        if (string.IsNullOrWhiteSpace(probe.ClientId))
            throw new ProviderConfigurationException("cmi", $"{CMIOptions.ConfigSection}:ClientId is required");
        if (string.IsNullOrWhiteSpace(probe.StoreKey))
            throw new ProviderConfigurationException("cmi", $"{CMIOptions.ConfigSection}:StoreKey is required");

        services.AddBhenguInMemoryCache();

        services.AddHttpClient<CMIPaymentProvider>();
        services.AddHttpClient<CMIThreeDSecureProvider>();
        services.AddHttpClient<CMISettlementProvider>();

        services.AddTransient<IPaymentGatewayProvider, CMIPaymentProvider>(sp =>
            sp.GetRequiredService<CMIPaymentProvider>());
        services.AddTransient<IThreeDSecureProvider, CMIThreeDSecureProvider>(sp =>
            sp.GetRequiredService<CMIThreeDSecureProvider>());
        services.AddTransient<ISettlementProvider, CMISettlementProvider>(sp =>
            sp.GetRequiredService<CMISettlementProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.CMI,
            (sp, _) => sp.GetRequiredService<CMIPaymentProvider>());
        services.AddKeyedTransient<IThreeDSecureProvider>(ProviderNames.CMI,
            (sp, _) => sp.GetRequiredService<CMIThreeDSecureProvider>());
        services.AddKeyedTransient<ISettlementProvider>(ProviderNames.CMI,
            (sp, _) => sp.GetRequiredService<CMISettlementProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
