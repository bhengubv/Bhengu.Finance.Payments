// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.PayUIndia.Configuration;
using Bhengu.Finance.Payments.PayUIndia.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.PayUIndia.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the PayU India provider. Reads configuration from <c>Bhengu:Finance:Payments:PayUIndia</c>.
    /// Fails fast at startup if required options (MerchantKey, Salt) are missing.
    /// </summary>
    public static IServiceCollection AddPayUIndiaPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(PayUIndiaOptions.ConfigSection);
        services.Configure<PayUIndiaOptions>(section);

        var probe = section.Get<PayUIndiaOptions>() ?? new PayUIndiaOptions();
        if (string.IsNullOrWhiteSpace(probe.MerchantKey))
            throw new ProviderConfigurationException("payuindia", $"{PayUIndiaOptions.ConfigSection}:MerchantKey is required");
        if (string.IsNullOrWhiteSpace(probe.Salt))
            throw new ProviderConfigurationException("payuindia", $"{PayUIndiaOptions.ConfigSection}:Salt is required");

        services.AddHttpClient<PayUIndiaPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, PayUIndiaPaymentProvider>(sp =>
            sp.GetRequiredService<PayUIndiaPaymentProvider>());
        services.AddTransient<IPayoutProvider, PayUIndiaPaymentProvider>(sp =>
            sp.GetRequiredService<PayUIndiaPaymentProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.PayUIndia, (sp, _) => sp.GetRequiredService<PayUIndiaPaymentProvider>());
        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
