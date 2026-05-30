// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.AirtelMoney.Configuration;
using Bhengu.Finance.Payments.AirtelMoney.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.AirtelMoney.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Airtel Money provider. Reads configuration from <c>Bhengu:Finance:Payments:AirtelMoney</c>.
    /// Fails fast at startup if required options (ClientId, ClientSecret, Country, Currency) are missing.
    /// </summary>
    public static IServiceCollection AddAirtelMoneyPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(AirtelMoneyOptions.ConfigSection);
        services.Configure<AirtelMoneyOptions>(section);

        var probe = section.Get<AirtelMoneyOptions>() ?? new AirtelMoneyOptions();
        if (string.IsNullOrWhiteSpace(probe.ClientId))
            throw new ProviderConfigurationException("airtelmoney", $"{AirtelMoneyOptions.ConfigSection}:ClientId is required");
        if (string.IsNullOrWhiteSpace(probe.ClientSecret))
            throw new ProviderConfigurationException("airtelmoney", $"{AirtelMoneyOptions.ConfigSection}:ClientSecret is required");
        if (string.IsNullOrWhiteSpace(probe.Country))
            throw new ProviderConfigurationException("airtelmoney", $"{AirtelMoneyOptions.ConfigSection}:Country is required");
        if (string.IsNullOrWhiteSpace(probe.Currency))
            throw new ProviderConfigurationException("airtelmoney", $"{AirtelMoneyOptions.ConfigSection}:Currency is required");

        services.AddHttpClient<AirtelMoneyPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, AirtelMoneyPaymentProvider>(sp =>
            sp.GetRequiredService<AirtelMoneyPaymentProvider>());
        services.AddTransient<IPayoutProvider, AirtelMoneyPaymentProvider>(sp =>
            sp.GetRequiredService<AirtelMoneyPaymentProvider>());

        return services;
    }
}
