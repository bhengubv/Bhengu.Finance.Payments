// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Paystack.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Paystack provider. Reads configuration from <c>Bhengu:Finance:Payments:Paystack</c>.
    /// Fails fast at startup if required options (SecretKey) are missing.
    /// </summary>
    public static IServiceCollection AddPaystackPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(PaystackOptions.ConfigSection);
        services.Configure<PaystackOptions>(section);

        var probe = section.Get<PaystackOptions>() ?? new PaystackOptions();
        if (string.IsNullOrWhiteSpace(probe.SecretKey))
            throw new ProviderConfigurationException("paystack", $"{PaystackOptions.ConfigSection}:SecretKey is required");

        services.AddHttpClient<PaystackPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, PaystackPaymentProvider>(sp =>
            sp.GetRequiredService<PaystackPaymentProvider>());
        services.AddTransient<IPayoutProvider, PaystackPaymentProvider>(sp =>
            sp.GetRequiredService<PaystackPaymentProvider>());

        return services;
    }
}
