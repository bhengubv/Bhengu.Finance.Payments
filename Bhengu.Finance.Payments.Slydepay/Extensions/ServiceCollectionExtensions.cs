// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Slydepay.Configuration;
using Bhengu.Finance.Payments.Slydepay.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Slydepay.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Slydepay provider. Reads configuration from <c>Bhengu:Finance:Payments:Slydepay</c>.
    /// Fails fast at startup if required options (EmailOrMobile, MerchantKey) are missing.
    /// </summary>
    public static IServiceCollection AddSlydepayPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SlydepayOptions.ConfigSection);
        services.Configure<SlydepayOptions>(section);

        var probe = section.Get<SlydepayOptions>() ?? new SlydepayOptions();
        if (string.IsNullOrWhiteSpace(probe.EmailOrMobile))
            throw new ProviderConfigurationException("slydepay", $"{SlydepayOptions.ConfigSection}:EmailOrMobile is required");
        if (string.IsNullOrWhiteSpace(probe.MerchantKey))
            throw new ProviderConfigurationException("slydepay", $"{SlydepayOptions.ConfigSection}:MerchantKey is required");

        services.AddHttpClient<SlydepayPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, SlydepayPaymentProvider>(sp =>
            sp.GetRequiredService<SlydepayPaymentProvider>());

        return services;
    }
}
