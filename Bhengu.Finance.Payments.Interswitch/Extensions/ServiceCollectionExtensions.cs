// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Interswitch.Configuration;
using Bhengu.Finance.Payments.Interswitch.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Interswitch.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Interswitch provider. Reads configuration from <c>Bhengu:Finance:Payments:Interswitch</c>.
    /// Fails fast at startup if required options (ClientId, ClientSecret) are missing.
    /// </summary>
    public static IServiceCollection AddInterswitchPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(InterswitchOptions.ConfigSection);
        services.Configure<InterswitchOptions>(section);

        var probe = section.Get<InterswitchOptions>() ?? new InterswitchOptions();
        if (string.IsNullOrWhiteSpace(probe.ClientId))
            throw new ProviderConfigurationException("interswitch", $"{InterswitchOptions.ConfigSection}:ClientId is required");
        if (string.IsNullOrWhiteSpace(probe.ClientSecret))
            throw new ProviderConfigurationException("interswitch", $"{InterswitchOptions.ConfigSection}:ClientSecret is required");

        services.AddHttpClient<InterswitchPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, InterswitchPaymentProvider>(sp =>
            sp.GetRequiredService<InterswitchPaymentProvider>());
        services.AddTransient<IPayoutProvider, InterswitchPaymentProvider>(sp =>
            sp.GetRequiredService<InterswitchPaymentProvider>());

        return services;
    }
}
