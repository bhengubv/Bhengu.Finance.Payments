// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.JamboPay.Configuration;
using Bhengu.Finance.Payments.JamboPay.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.JamboPay.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the JamboPay provider. Reads configuration from <c>Bhengu:Finance:Payments:JamboPay</c>.
    /// Fails fast at startup if required options (ApiKey, ClientId, ClientSecret, MerchantCode) are missing.
    /// </summary>
    public static IServiceCollection AddJamboPayPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(JamboPayOptions.ConfigSection);
        services.Configure<JamboPayOptions>(section);

        var probe = section.Get<JamboPayOptions>() ?? new JamboPayOptions();
        if (string.IsNullOrWhiteSpace(probe.ApiKey))
            throw new ProviderConfigurationException("jambopay", $"{JamboPayOptions.ConfigSection}:ApiKey is required");
        if (string.IsNullOrWhiteSpace(probe.ClientId))
            throw new ProviderConfigurationException("jambopay", $"{JamboPayOptions.ConfigSection}:ClientId is required");
        if (string.IsNullOrWhiteSpace(probe.ClientSecret))
            throw new ProviderConfigurationException("jambopay", $"{JamboPayOptions.ConfigSection}:ClientSecret is required");
        if (string.IsNullOrWhiteSpace(probe.MerchantCode))
            throw new ProviderConfigurationException("jambopay", $"{JamboPayOptions.ConfigSection}:MerchantCode is required");

        services.AddHttpClient<JamboPayPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, JamboPayPaymentProvider>(sp =>
            sp.GetRequiredService<JamboPayPaymentProvider>());
        services.AddTransient<IPayoutProvider, JamboPayPaymentProvider>(sp =>
            sp.GetRequiredService<JamboPayPaymentProvider>());

        return services;
    }
}
