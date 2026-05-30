// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Bhengu.Finance.Payments.Flutterwave.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Flutterwave.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Flutterwave provider. Reads configuration from <c>Bhengu:Finance:Payments:Flutterwave</c>.
    /// Fails fast at startup if required options (SecretKey) are missing.
    /// </summary>
    public static IServiceCollection AddFlutterwavePayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(FlutterwaveOptions.ConfigSection);
        services.Configure<FlutterwaveOptions>(section);

        var probe = section.Get<FlutterwaveOptions>() ?? new FlutterwaveOptions();
        if (string.IsNullOrWhiteSpace(probe.SecretKey))
            throw new ProviderConfigurationException("flutterwave", $"{FlutterwaveOptions.ConfigSection}:SecretKey is required");

        services.AddHttpClient<FlutterwavePaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, FlutterwavePaymentProvider>(sp =>
            sp.GetRequiredService<FlutterwavePaymentProvider>());
        services.AddTransient<IPayoutProvider, FlutterwavePaymentProvider>(sp =>
            sp.GetRequiredService<FlutterwavePaymentProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Flutterwave, (sp, _) => sp.GetRequiredService<FlutterwavePaymentProvider>());
        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
