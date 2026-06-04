// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Remita.Configuration;
using Bhengu.Finance.Payments.Remita.Internals;
using Bhengu.Finance.Payments.Remita.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Remita.Extensions;

/// <summary>DI registration helpers for the Remita provider family.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Remita provider. Reads configuration from <c>Bhengu:Finance:Payments:Remita</c>.
    /// Fails fast at startup if required options (MerchantId, ServiceTypeId, ApiKey) are missing.
    /// Registers charge + refund + payout + mandate + settlement, keyed by
    /// <see cref="ProviderNames.Remita"/>.
    /// </summary>
    public static IServiceCollection AddRemitaPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(RemitaOptions.ConfigSection);
        services.Configure<RemitaOptions>(section);

        var probe = section.Get<RemitaOptions>() ?? new RemitaOptions();
        if (string.IsNullOrWhiteSpace(probe.MerchantId))
            throw new ProviderConfigurationException("remita", $"{RemitaOptions.ConfigSection}:MerchantId is required");
        if (string.IsNullOrWhiteSpace(probe.ServiceTypeId))
            throw new ProviderConfigurationException("remita", $"{RemitaOptions.ConfigSection}:ServiceTypeId is required");
        if (string.IsNullOrWhiteSpace(probe.ApiKey))
            throw new ProviderConfigurationException("remita", $"{RemitaOptions.ConfigSection}:ApiKey is required");

        services.AddBhenguInMemoryCache();
        services.AddSingleton<RemitaIdempotencyCache>();

        services.AddHttpClient<RemitaPaymentProvider>();
        services.AddHttpClient<RemitaMandateProvider>();
        services.AddHttpClient<RemitaSettlementProvider>();

        services.AddTransient<IPaymentGatewayProvider, RemitaPaymentProvider>(sp =>
            sp.GetRequiredService<RemitaPaymentProvider>());
        services.AddTransient<IPayoutProvider, RemitaPaymentProvider>(sp =>
            sp.GetRequiredService<RemitaPaymentProvider>());
        services.AddTransient<IMandateProvider, RemitaMandateProvider>(sp =>
            sp.GetRequiredService<RemitaMandateProvider>());
        services.AddTransient<ISettlementProvider, RemitaSettlementProvider>(sp =>
            sp.GetRequiredService<RemitaSettlementProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Remita,
            (sp, _) => sp.GetRequiredService<RemitaPaymentProvider>());
        services.AddKeyedTransient<IPayoutProvider>(ProviderNames.Remita,
            (sp, _) => sp.GetRequiredService<RemitaPaymentProvider>());
        services.AddKeyedTransient<IMandateProvider>(ProviderNames.Remita,
            (sp, _) => sp.GetRequiredService<RemitaMandateProvider>());
        services.AddKeyedTransient<ISettlementProvider>(ProviderNames.Remita,
            (sp, _) => sp.GetRequiredService<RemitaSettlementProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
