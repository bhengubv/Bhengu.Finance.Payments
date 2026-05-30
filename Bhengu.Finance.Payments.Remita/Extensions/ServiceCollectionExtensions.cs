// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Remita.Configuration;
using Bhengu.Finance.Payments.Remita.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Remita.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Remita provider. Reads configuration from <c>Bhengu:Finance:Payments:Remita</c>.
    /// Fails fast at startup if required options (MerchantId, ServiceTypeId, ApiKey) are missing.
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

        services.AddHttpClient<RemitaPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, RemitaPaymentProvider>(sp =>
            sp.GetRequiredService<RemitaPaymentProvider>());
        services.AddTransient<IPayoutProvider, RemitaPaymentProvider>(sp =>
            sp.GetRequiredService<RemitaPaymentProvider>());

        return services;
    }
}
