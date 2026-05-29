// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.BricsPay.Configuration;
using Bhengu.Finance.Payments.BricsPay.Currency;
using Bhengu.Finance.Payments.BricsPay.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.BricsPay.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the BRICS Pay provider plus its supporting ICurrencyExchangeService.
    /// Reads configuration from <c>Bhengu:Finance:Payments:BricsPay</c>.
    /// </summary>
    public static IServiceCollection AddBricsPayPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(BricsPayOptions.ConfigSection);
        services.Configure<BricsPayOptions>(section);

        var probe = section.Get<BricsPayOptions>() ?? new BricsPayOptions();
        if (string.IsNullOrWhiteSpace(probe.MerchantId))
            throw new ProviderConfigurationException("bricspay", $"{BricsPayOptions.ConfigSection}:MerchantId is required");
        if (string.IsNullOrWhiteSpace(probe.SecretKey))
            throw new ProviderConfigurationException("bricspay", $"{BricsPayOptions.ConfigSection}:SecretKey is required");

        services.AddHttpClient<ICurrencyExchangeService, CurrencyExchangeService>();
        services.AddHttpClient<BricsPayPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, BricsPayPaymentProvider>(sp =>
            sp.GetRequiredService<BricsPayPaymentProvider>());
        services.AddTransient<IPayoutProvider, BricsPayPaymentProvider>(sp =>
            sp.GetRequiredService<BricsPayPaymentProvider>());

        return services;
    }
}
