// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.IPay.Configuration;
using Bhengu.Finance.Payments.IPay.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.IPay.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the iPay (Africa) provider. Reads configuration from <c>Bhengu:Finance:Payments:IPay</c>.
    /// Fails fast at startup if required options (VendorId, HashKey) are missing.
    /// </summary>
    public static IServiceCollection AddIPayPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(IPayOptions.ConfigSection);
        services.Configure<IPayOptions>(section);

        var probe = section.Get<IPayOptions>() ?? new IPayOptions();
        if (string.IsNullOrWhiteSpace(probe.VendorId))
            throw new ProviderConfigurationException("ipay", $"{IPayOptions.ConfigSection}:VendorId is required");
        if (string.IsNullOrWhiteSpace(probe.HashKey))
            throw new ProviderConfigurationException("ipay", $"{IPayOptions.ConfigSection}:HashKey is required");

        services.AddHttpClient<IPayPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, IPayPaymentProvider>(sp =>
            sp.GetRequiredService<IPayPaymentProvider>());

        return services;
    }
}
