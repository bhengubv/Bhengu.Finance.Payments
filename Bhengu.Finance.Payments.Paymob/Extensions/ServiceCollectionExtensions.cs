// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Paymob.Configuration;
using Bhengu.Finance.Payments.Paymob.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Paymob.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Paymob provider. Reads configuration from <c>Bhengu:Finance:Payments:Paymob</c>.
    /// Fails fast at startup if required options (ApiKey) are missing.
    /// </summary>
    public static IServiceCollection AddPaymobPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(PaymobOptions.ConfigSection);
        services.Configure<PaymobOptions>(section);

        var probe = section.Get<PaymobOptions>() ?? new PaymobOptions();
        if (string.IsNullOrWhiteSpace(probe.ApiKey))
            throw new ProviderConfigurationException("paymob", $"{PaymobOptions.ConfigSection}:ApiKey is required");

        services.AddHttpClient<PaymobPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, PaymobPaymentProvider>(sp =>
            sp.GetRequiredService<PaymobPaymentProvider>());
        services.AddTransient<IPayoutProvider, PaymobPaymentProvider>(sp =>
            sp.GetRequiredService<PaymobPaymentProvider>());
        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Paymob, (sp, _) => sp.GetRequiredService<PaymobPaymentProvider>());
        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
