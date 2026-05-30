// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.DPO.Configuration;
using Bhengu.Finance.Payments.DPO.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.DPO.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the DPO Group provider. Reads configuration from <c>Bhengu:Finance:Payments:DPO</c>.
    /// Fails fast at startup if required options (CompanyToken) are missing.
    /// </summary>
    public static IServiceCollection AddDPOPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(DPOOptions.ConfigSection);
        services.Configure<DPOOptions>(section);

        var probe = section.Get<DPOOptions>() ?? new DPOOptions();
        if (string.IsNullOrWhiteSpace(probe.CompanyToken))
            throw new ProviderConfigurationException("dpo", $"{DPOOptions.ConfigSection}:CompanyToken is required");

        services.AddHttpClient<DPOPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, DPOPaymentProvider>(sp =>
            sp.GetRequiredService<DPOPaymentProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.DPO, (sp, _) => sp.GetRequiredService<DPOPaymentProvider>());
        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
