// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.ChipperCash.Configuration;
using Bhengu.Finance.Payments.ChipperCash.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.ChipperCash.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Chipper Cash provider. Reads configuration from <c>Bhengu:Finance:Payments:ChipperCash</c>.
    /// Fails fast at startup if required options (ApiKey, ApiSecret) are missing.
    /// </summary>
    public static IServiceCollection AddChipperCashPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ChipperCashOptions.ConfigSection);
        services.Configure<ChipperCashOptions>(section);

        var probe = section.Get<ChipperCashOptions>() ?? new ChipperCashOptions();
        if (string.IsNullOrWhiteSpace(probe.ApiKey))
            throw new ProviderConfigurationException("chippercash", $"{ChipperCashOptions.ConfigSection}:ApiKey is required");
        if (string.IsNullOrWhiteSpace(probe.ApiSecret))
            throw new ProviderConfigurationException("chippercash", $"{ChipperCashOptions.ConfigSection}:ApiSecret is required");

        services.AddHttpClient<ChipperCashPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, ChipperCashPaymentProvider>(sp =>
            sp.GetRequiredService<ChipperCashPaymentProvider>());
        services.AddTransient<IPayoutProvider, ChipperCashPaymentProvider>(sp =>
            sp.GetRequiredService<ChipperCashPaymentProvider>());

        return services;
    }
}
