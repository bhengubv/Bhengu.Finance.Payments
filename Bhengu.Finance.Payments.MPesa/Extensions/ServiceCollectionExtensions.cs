// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.MPesa.Configuration;
using Bhengu.Finance.Payments.MPesa.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.MPesa.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Safaricom M-Pesa (Daraja) provider. Reads configuration from <c>Bhengu:Finance:Payments:MPesa</c>.
    /// Fails fast at startup if required options (ConsumerKey, ConsumerSecret, BusinessShortCode, Passkey) are missing.
    /// </summary>
    public static IServiceCollection AddMPesaPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(MPesaOptions.ConfigSection);
        services.Configure<MPesaOptions>(section);

        var probe = section.Get<MPesaOptions>() ?? new MPesaOptions();
        if (string.IsNullOrWhiteSpace(probe.ConsumerKey))
            throw new ProviderConfigurationException("mpesa", $"{MPesaOptions.ConfigSection}:ConsumerKey is required");
        if (string.IsNullOrWhiteSpace(probe.ConsumerSecret))
            throw new ProviderConfigurationException("mpesa", $"{MPesaOptions.ConfigSection}:ConsumerSecret is required");
        if (string.IsNullOrWhiteSpace(probe.BusinessShortCode))
            throw new ProviderConfigurationException("mpesa", $"{MPesaOptions.ConfigSection}:BusinessShortCode is required");
        if (string.IsNullOrWhiteSpace(probe.Passkey))
            throw new ProviderConfigurationException("mpesa", $"{MPesaOptions.ConfigSection}:Passkey is required");

        services.AddHttpClient<MPesaPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, MPesaPaymentProvider>(sp =>
            sp.GetRequiredService<MPesaPaymentProvider>());
        services.AddTransient<IPayoutProvider, MPesaPaymentProvider>(sp =>
            sp.GetRequiredService<MPesaPaymentProvider>());

        return services;
    }
}
