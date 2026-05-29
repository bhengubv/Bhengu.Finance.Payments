// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.PayJustNow.Configuration;
using Bhengu.Finance.Payments.PayJustNow.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.PayJustNow.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the PayJustNow provider. Reads configuration from <c>Bhengu:Finance:Payments:PayJustNow</c>.
    /// Fails fast at startup if required options (ApiKey, MerchantId) are missing.
    /// </summary>
    public static IServiceCollection AddPayJustNowPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(PayJustNowOptions.ConfigSection);
        services.Configure<PayJustNowOptions>(section);

        var probe = section.Get<PayJustNowOptions>() ?? new PayJustNowOptions();
        if (string.IsNullOrWhiteSpace(probe.ApiKey))
            throw new ProviderConfigurationException("payjustnow", $"{PayJustNowOptions.ConfigSection}:ApiKey is required");
        if (string.IsNullOrWhiteSpace(probe.MerchantId))
            throw new ProviderConfigurationException("payjustnow", $"{PayJustNowOptions.ConfigSection}:MerchantId is required");

        services.AddHttpClient<PayJustNowPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, PayJustNowPaymentProvider>(sp =>
            sp.GetRequiredService<PayJustNowPaymentProvider>());

        return services;
    }
}
