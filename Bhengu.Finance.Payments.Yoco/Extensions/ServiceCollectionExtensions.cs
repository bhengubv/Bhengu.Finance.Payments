// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Yoco.Configuration;
using Bhengu.Finance.Payments.Yoco.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Yoco.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Yoco provider. Reads configuration from <c>Bhengu:Finance:Payments:Yoco</c>.
    /// Fails fast at startup if required options (SecretKey) are missing.
    /// </summary>
    public static IServiceCollection AddYocoPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(YocoOptions.ConfigSection);
        services.Configure<YocoOptions>(section);

        var probe = section.Get<YocoOptions>() ?? new YocoOptions();
        if (string.IsNullOrWhiteSpace(probe.SecretKey))
            throw new ProviderConfigurationException("yoco", $"{YocoOptions.ConfigSection}:SecretKey is required");

        services.AddHttpClient<YocoPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, YocoPaymentProvider>(sp =>
            sp.GetRequiredService<YocoPaymentProvider>());

        // Register as keyed service so consumers can resolve by name: [FromKeyedServices(ProviderNames.Yoco)]
        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Yoco, (sp, _) => sp.GetRequiredService<YocoPaymentProvider>());

        // Eager startup validation — fails the app at startup if config is broken (vs first request)
        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
