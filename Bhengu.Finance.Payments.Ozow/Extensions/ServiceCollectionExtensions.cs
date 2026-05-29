// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Ozow.Configuration;
using Bhengu.Finance.Payments.Ozow.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Ozow.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Ozow provider. Reads configuration from <c>Bhengu:Finance:Payments:Ozow</c>.
    /// Fails fast at startup if required options (SiteCode, PrivateKey, ApiKey) are missing.
    /// </summary>
    public static IServiceCollection AddOzowPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(OzowOptions.ConfigSection);
        services.Configure<OzowOptions>(section);

        var probe = section.Get<OzowOptions>() ?? new OzowOptions();
        if (string.IsNullOrWhiteSpace(probe.SiteCode))
            throw new ProviderConfigurationException("ozow", $"{OzowOptions.ConfigSection}:SiteCode is required");
        if (string.IsNullOrWhiteSpace(probe.PrivateKey))
            throw new ProviderConfigurationException("ozow", $"{OzowOptions.ConfigSection}:PrivateKey is required");
        if (string.IsNullOrWhiteSpace(probe.ApiKey))
            throw new ProviderConfigurationException("ozow", $"{OzowOptions.ConfigSection}:ApiKey is required");

        services.AddHttpClient<OzowPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, OzowPaymentProvider>(sp =>
            sp.GetRequiredService<OzowPaymentProvider>());

        return services;
    }
}
