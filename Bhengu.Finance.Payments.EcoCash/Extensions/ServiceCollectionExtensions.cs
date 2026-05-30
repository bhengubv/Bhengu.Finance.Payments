// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.EcoCash.Configuration;
using Bhengu.Finance.Payments.EcoCash.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.EcoCash.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the EcoCash provider. Reads configuration from <c>Bhengu:Finance:Payments:EcoCash</c>.
    /// Fails fast at startup if required options (ApiKey, MerchantCode) are missing.
    /// </summary>
    public static IServiceCollection AddEcoCashPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(EcoCashOptions.ConfigSection);
        services.Configure<EcoCashOptions>(section);

        var probe = section.Get<EcoCashOptions>() ?? new EcoCashOptions();
        if (string.IsNullOrWhiteSpace(probe.ApiKey))
            throw new ProviderConfigurationException("ecocash", $"{EcoCashOptions.ConfigSection}:ApiKey is required");
        if (string.IsNullOrWhiteSpace(probe.MerchantCode))
            throw new ProviderConfigurationException("ecocash", $"{EcoCashOptions.ConfigSection}:MerchantCode is required");

        services.AddHttpClient<EcoCashPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, EcoCashPaymentProvider>(sp =>
            sp.GetRequiredService<EcoCashPaymentProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.EcoCash, (sp, _) => sp.GetRequiredService<EcoCashPaymentProvider>());
        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
