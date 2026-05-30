// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.ExpressPay.Configuration;
using Bhengu.Finance.Payments.ExpressPay.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.ExpressPay.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the ExpressPay provider. Reads configuration from <c>Bhengu:Finance:Payments:ExpressPay</c>.
    /// Fails fast at startup if required options (MerchantId, ApiKey) are missing.
    /// </summary>
    public static IServiceCollection AddExpressPayPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ExpressPayOptions.ConfigSection);
        services.Configure<ExpressPayOptions>(section);

        var probe = section.Get<ExpressPayOptions>() ?? new ExpressPayOptions();
        if (string.IsNullOrWhiteSpace(probe.MerchantId))
            throw new ProviderConfigurationException("expresspay", $"{ExpressPayOptions.ConfigSection}:MerchantId is required");
        if (string.IsNullOrWhiteSpace(probe.ApiKey))
            throw new ProviderConfigurationException("expresspay", $"{ExpressPayOptions.ConfigSection}:ApiKey is required");

        services.AddHttpClient<ExpressPayPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, ExpressPayPaymentProvider>(sp =>
            sp.GetRequiredService<ExpressPayPaymentProvider>());

        return services;
    }
}
