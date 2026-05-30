// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Mukuru.Configuration;
using Bhengu.Finance.Payments.Mukuru.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Mukuru.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Mukuru provider. Reads configuration from <c>Bhengu:Finance:Payments:Mukuru</c>.
    /// Fails fast at startup if required options (ClientId, ClientSecret) are missing.
    /// </summary>
    public static IServiceCollection AddMukuruPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(MukuruOptions.ConfigSection);
        services.Configure<MukuruOptions>(section);

        var probe = section.Get<MukuruOptions>() ?? new MukuruOptions();
        if (string.IsNullOrWhiteSpace(probe.ClientId))
            throw new ProviderConfigurationException("mukuru", $"{MukuruOptions.ConfigSection}:ClientId is required");
        if (string.IsNullOrWhiteSpace(probe.ClientSecret))
            throw new ProviderConfigurationException("mukuru", $"{MukuruOptions.ConfigSection}:ClientSecret is required");

        services.AddHttpClient<MukuruPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, MukuruPaymentProvider>(sp =>
            sp.GetRequiredService<MukuruPaymentProvider>());
        services.AddTransient<IPayoutProvider, MukuruPaymentProvider>(sp =>
            sp.GetRequiredService<MukuruPaymentProvider>());

        // Register as keyed service so consumers can resolve by name: [FromKeyedServices(ProviderNames.Mukuru)]
        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Mukuru, (sp, _) => sp.GetRequiredService<MukuruPaymentProvider>());

        // Eager startup validation — fails the app at startup if config is broken (vs first request)
        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
