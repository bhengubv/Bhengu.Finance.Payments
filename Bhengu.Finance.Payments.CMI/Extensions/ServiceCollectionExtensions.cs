// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.CMI.Configuration;
using Bhengu.Finance.Payments.CMI.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.CMI.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the CMI provider. Reads configuration from <c>Bhengu:Finance:Payments:CMI</c>.
    /// Fails fast at startup if required options (ClientId, StoreKey) are missing.
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

        services.AddHttpClient<CMIPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, CMIPaymentProvider>(sp =>
            sp.GetRequiredService<CMIPaymentProvider>());

        return services;
    }
}
