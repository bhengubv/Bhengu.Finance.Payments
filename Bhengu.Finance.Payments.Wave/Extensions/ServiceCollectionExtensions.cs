// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Wave.Configuration;
using Bhengu.Finance.Payments.Wave.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Wave.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Wave provider. Reads configuration from <c>Bhengu:Finance:Payments:Wave</c>.
    /// Fails fast at startup if required options (ApiKey) are missing.
    /// </summary>
    public static IServiceCollection AddWavePayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(WaveOptions.ConfigSection);
        services.Configure<WaveOptions>(section);

        var probe = section.Get<WaveOptions>() ?? new WaveOptions();
        if (string.IsNullOrWhiteSpace(probe.ApiKey))
            throw new ProviderConfigurationException("wave", $"{WaveOptions.ConfigSection}:ApiKey is required");

        services.AddHttpClient<WavePaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, WavePaymentProvider>(sp =>
            sp.GetRequiredService<WavePaymentProvider>());
        services.AddTransient<IPayoutProvider, WavePaymentProvider>(sp =>
            sp.GetRequiredService<WavePaymentProvider>());

        return services;
    }
}
