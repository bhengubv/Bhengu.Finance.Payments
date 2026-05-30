// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Stitch.Configuration;
using Bhengu.Finance.Payments.Stitch.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Stitch.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Stitch provider. Reads configuration from <c>Bhengu:Finance:Payments:Stitch</c>.
    /// Fails fast at startup if required options (ClientId, plus either ApiKey or ClientAssertionJwt) are missing.
    /// </summary>
    public static IServiceCollection AddStitchPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(StitchOptions.ConfigSection);
        services.Configure<StitchOptions>(section);

        var probe = section.Get<StitchOptions>() ?? new StitchOptions();
        if (string.IsNullOrWhiteSpace(probe.ClientId))
            throw new ProviderConfigurationException("stitch", $"{StitchOptions.ConfigSection}:ClientId is required");
        if (string.IsNullOrWhiteSpace(probe.ApiKey) && string.IsNullOrWhiteSpace(probe.ClientAssertionJwt))
            throw new ProviderConfigurationException("stitch",
                $"{StitchOptions.ConfigSection}:ApiKey or ClientAssertionJwt is required");

        services.AddHttpClient<StitchPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, StitchPaymentProvider>(sp =>
            sp.GetRequiredService<StitchPaymentProvider>());
        services.AddTransient<IPayoutProvider, StitchPaymentProvider>(sp =>
            sp.GetRequiredService<StitchPaymentProvider>());

        return services;
    }
}
