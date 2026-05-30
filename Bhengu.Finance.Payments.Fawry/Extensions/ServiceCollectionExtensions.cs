// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Fawry.Configuration;
using Bhengu.Finance.Payments.Fawry.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Fawry.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Fawry provider. Reads configuration from <c>Bhengu:Finance:Payments:Fawry</c>.
    /// Fails fast at startup if required options (MerchantCode, SecurityKey) are missing.
    /// </summary>
    public static IServiceCollection AddFawryPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(FawryOptions.ConfigSection);
        services.Configure<FawryOptions>(section);

        var probe = section.Get<FawryOptions>() ?? new FawryOptions();
        if (string.IsNullOrWhiteSpace(probe.MerchantCode))
            throw new ProviderConfigurationException("fawry", $"{FawryOptions.ConfigSection}:MerchantCode is required");
        if (string.IsNullOrWhiteSpace(probe.SecurityKey))
            throw new ProviderConfigurationException("fawry", $"{FawryOptions.ConfigSection}:SecurityKey is required");

        services.AddHttpClient<FawryPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, FawryPaymentProvider>(sp =>
            sp.GetRequiredService<FawryPaymentProvider>());

        return services;
    }
}
