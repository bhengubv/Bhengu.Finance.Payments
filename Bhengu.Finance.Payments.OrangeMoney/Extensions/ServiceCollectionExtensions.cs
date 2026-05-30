// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.OrangeMoney.Configuration;
using Bhengu.Finance.Payments.OrangeMoney.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.OrangeMoney.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Orange Money Web Payment provider. Reads configuration from <c>Bhengu:Finance:Payments:OrangeMoney</c>.
    /// Fails fast at startup if required options (ConsumerKey, ConsumerSecret, MerchantKey, Country) are missing.
    /// </summary>
    public static IServiceCollection AddOrangeMoneyPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(OrangeMoneyOptions.ConfigSection);
        services.Configure<OrangeMoneyOptions>(section);

        var probe = section.Get<OrangeMoneyOptions>() ?? new OrangeMoneyOptions();
        if (string.IsNullOrWhiteSpace(probe.ConsumerKey))
            throw new ProviderConfigurationException("orangemoney", $"{OrangeMoneyOptions.ConfigSection}:ConsumerKey is required");
        if (string.IsNullOrWhiteSpace(probe.ConsumerSecret))
            throw new ProviderConfigurationException("orangemoney", $"{OrangeMoneyOptions.ConfigSection}:ConsumerSecret is required");
        if (string.IsNullOrWhiteSpace(probe.MerchantKey))
            throw new ProviderConfigurationException("orangemoney", $"{OrangeMoneyOptions.ConfigSection}:MerchantKey is required");
        if (string.IsNullOrWhiteSpace(probe.Country))
            throw new ProviderConfigurationException("orangemoney", $"{OrangeMoneyOptions.ConfigSection}:Country is required");

        services.AddHttpClient<OrangeMoneyPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, OrangeMoneyPaymentProvider>(sp =>
            sp.GetRequiredService<OrangeMoneyPaymentProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.OrangeMoney, (sp, _) => sp.GetRequiredService<OrangeMoneyPaymentProvider>());
        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
