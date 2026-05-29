// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.PayFast.Builders;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Bhengu.Finance.Payments.PayFast.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.PayFast.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the PayFast provider. Reads configuration from <c>Bhengu:Finance:Payments:PayFast</c>.
    /// Fails fast at startup if required options (MerchantId) are missing.
    /// </summary>
    public static IServiceCollection AddPayFastPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(PayFastOptions.ConfigSection);
        services.Configure<PayFastOptions>(section);

        // Fail fast — read the bound options and validate before the app starts taking traffic.
        var probe = section.Get<PayFastOptions>() ?? new PayFastOptions();
        if (string.IsNullOrWhiteSpace(probe.MerchantId))
            throw new ProviderConfigurationException("payfast", $"{PayFastOptions.ConfigSection}:MerchantId is required");

        services.AddHttpClient<PayFastPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, PayFastPaymentProvider>(sp =>
            sp.GetRequiredService<PayFastPaymentProvider>());
        services.AddTransient<PayFastFormBuilder>();

        return services;
    }
}
