// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.MercadoPago.Configuration;
using Bhengu.Finance.Payments.MercadoPago.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.MercadoPago.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Mercado Pago provider. Reads configuration from <c>Bhengu:Finance:Payments:MercadoPago</c>.
    /// Fails fast at startup if required options (AccessToken) are missing.
    /// </summary>
    public static IServiceCollection AddMercadoPagoPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(MercadoPagoOptions.ConfigSection);
        services.Configure<MercadoPagoOptions>(section);

        var probe = section.Get<MercadoPagoOptions>() ?? new MercadoPagoOptions();
        if (string.IsNullOrWhiteSpace(probe.AccessToken))
            throw new ProviderConfigurationException("mercadopago", $"{MercadoPagoOptions.ConfigSection}:AccessToken is required");

        services.AddHttpClient<MercadoPagoPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, MercadoPagoPaymentProvider>(sp =>
            sp.GetRequiredService<MercadoPagoPaymentProvider>());
        services.AddTransient<IPayoutProvider, MercadoPagoPaymentProvider>(sp =>
            sp.GetRequiredService<MercadoPagoPaymentProvider>());

        return services;
    }
}
