// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Cellulant.Configuration;
using Bhengu.Finance.Payments.Cellulant.Internals;
using Bhengu.Finance.Payments.Cellulant.Providers;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Cellulant.Extensions;

/// <summary>DI registration helpers for the Cellulant (Tingg / Mula) provider family.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Cellulant (Tingg / Mula) provider family (payment + payout + settlement +
    /// marketplace). Reads configuration from <c>Bhengu:Finance:Payments:Cellulant</c>. Fails fast
    /// at startup if required options (ServiceCode, ClientId, ClientSecret) are missing.
    /// </summary>
    public static IServiceCollection AddCellulantPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(CellulantOptions.ConfigSection);
        services.Configure<CellulantOptions>(section);

        var probe = section.Get<CellulantOptions>() ?? new CellulantOptions();
        if (string.IsNullOrWhiteSpace(probe.ServiceCode))
            throw new ProviderConfigurationException("cellulant", $"{CellulantOptions.ConfigSection}:ServiceCode is required");
        if (string.IsNullOrWhiteSpace(probe.ClientId))
            throw new ProviderConfigurationException("cellulant", $"{CellulantOptions.ConfigSection}:ClientId is required");
        if (string.IsNullOrWhiteSpace(probe.ClientSecret))
            throw new ProviderConfigurationException("cellulant", $"{CellulantOptions.ConfigSection}:ClientSecret is required");

        services.AddBhenguInMemoryCache();
        services.AddSingleton<CellulantTokenBroker>();

        services.AddHttpClient<CellulantPaymentProvider>();
        services.AddHttpClient<CellulantSettlementProvider>();
        services.AddHttpClient<CellulantMarketplaceProvider>();

        services.AddTransient<IPaymentGatewayProvider, CellulantPaymentProvider>(sp =>
            sp.GetRequiredService<CellulantPaymentProvider>());
        services.AddTransient<IPayoutProvider, CellulantPaymentProvider>(sp =>
            sp.GetRequiredService<CellulantPaymentProvider>());
        services.AddTransient<ISettlementProvider, CellulantSettlementProvider>(sp =>
            sp.GetRequiredService<CellulantSettlementProvider>());
        services.AddTransient<IMarketplaceProvider, CellulantMarketplaceProvider>(sp =>
            sp.GetRequiredService<CellulantMarketplaceProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Cellulant,
            (sp, _) => sp.GetRequiredService<CellulantPaymentProvider>());
        services.AddKeyedTransient<IPayoutProvider>(ProviderNames.Cellulant,
            (sp, _) => sp.GetRequiredService<CellulantPaymentProvider>());
        services.AddKeyedTransient<ISettlementProvider>(ProviderNames.Cellulant,
            (sp, _) => sp.GetRequiredService<CellulantSettlementProvider>());
        services.AddKeyedTransient<IMarketplaceProvider>(ProviderNames.Cellulant,
            (sp, _) => sp.GetRequiredService<CellulantMarketplaceProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
