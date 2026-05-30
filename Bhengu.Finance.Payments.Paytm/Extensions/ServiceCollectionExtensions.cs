// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Paytm.Configuration;
using Bhengu.Finance.Payments.Paytm.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Paytm.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Paytm provider. Reads configuration from <c>Bhengu:Finance:Payments:Paytm</c>.
    /// Fails fast at startup if required options (MerchantId, MerchantKey) are missing.
    /// </summary>
    public static IServiceCollection AddPaytmPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(PaytmOptions.ConfigSection);
        services.Configure<PaytmOptions>(section);

        var probe = section.Get<PaytmOptions>() ?? new PaytmOptions();
        if (string.IsNullOrWhiteSpace(probe.MerchantId))
            throw new ProviderConfigurationException("paytm", $"{PaytmOptions.ConfigSection}:MerchantId is required");
        if (string.IsNullOrWhiteSpace(probe.MerchantKey))
            throw new ProviderConfigurationException("paytm", $"{PaytmOptions.ConfigSection}:MerchantKey is required");

        services.AddHttpClient<PaytmPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, PaytmPaymentProvider>(sp =>
            sp.GetRequiredService<PaytmPaymentProvider>());
        services.AddTransient<IPayoutProvider, PaytmPaymentProvider>(sp =>
            sp.GetRequiredService<PaytmPaymentProvider>());

        return services;
    }
}
