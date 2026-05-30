// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Kashier.Configuration;
using Bhengu.Finance.Payments.Kashier.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Kashier.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Kashier provider. Reads configuration from <c>Bhengu:Finance:Payments:Kashier</c>.
    /// Fails fast at startup if required options (ApiKey, MerchantId) are missing.
    /// </summary>
    public static IServiceCollection AddKashierPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(KashierOptions.ConfigSection);
        services.Configure<KashierOptions>(section);

        var probe = section.Get<KashierOptions>() ?? new KashierOptions();
        if (string.IsNullOrWhiteSpace(probe.ApiKey))
            throw new ProviderConfigurationException("kashier", $"{KashierOptions.ConfigSection}:ApiKey is required");
        if (string.IsNullOrWhiteSpace(probe.MerchantId))
            throw new ProviderConfigurationException("kashier", $"{KashierOptions.ConfigSection}:MerchantId is required");

        services.AddHttpClient<KashierPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, KashierPaymentProvider>(sp =>
            sp.GetRequiredService<KashierPaymentProvider>());
        services.AddTransient<IPayoutProvider, KashierPaymentProvider>(sp =>
            sp.GetRequiredService<KashierPaymentProvider>());
        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Kashier, (sp, _) => sp.GetRequiredService<KashierPaymentProvider>());
        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
