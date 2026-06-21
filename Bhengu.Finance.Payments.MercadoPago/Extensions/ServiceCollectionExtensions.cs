// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
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

        services.AddBhenguInMemoryCache();
        services.AddHttpClient<MercadoPagoPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, MercadoPagoPaymentProvider>(sp =>
            sp.GetRequiredService<MercadoPagoPaymentProvider>());

        // Mercado Pago exposes no public disbursement endpoint, so no IPayoutProvider is registered.

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.MercadoPago, (sp, _) => sp.GetRequiredService<MercadoPagoPaymentProvider>());

        // Recurring billing (Preapproval).
        services.AddHttpClient<MercadoPagoSubscriptionProvider>();
        services.AddTransient<ISubscriptionProvider, MercadoPagoSubscriptionProvider>(sp => sp.GetRequiredService<MercadoPagoSubscriptionProvider>());
        services.AddKeyedTransient<ISubscriptionProvider>(ProviderNames.MercadoPago, (sp, _) => sp.GetRequiredService<MercadoPagoSubscriptionProvider>());

        // PIX QR code generation.
        services.AddHttpClient<MercadoPagoQrCodeProvider>();
        services.AddTransient<IQrCodeProvider, MercadoPagoQrCodeProvider>(sp => sp.GetRequiredService<MercadoPagoQrCodeProvider>());
        services.AddKeyedTransient<IQrCodeProvider>(ProviderNames.MercadoPago, (sp, _) => sp.GetRequiredService<MercadoPagoQrCodeProvider>());

        // Server-side card tokenisation (SAQ-D).
        services.AddHttpClient<MercadoPagoTokenisationProvider>();
        services.AddHttpClient<MercadoPagoRawCardTokenisationProvider>();
        services.AddTransient<ITokenisationProvider, MercadoPagoTokenisationProvider>(sp => sp.GetRequiredService<MercadoPagoTokenisationProvider>());
        services.AddTransient<IRawCardTokenisationProvider, MercadoPagoRawCardTokenisationProvider>(sp => sp.GetRequiredService<MercadoPagoRawCardTokenisationProvider>());
        services.AddKeyedTransient<ITokenisationProvider>(ProviderNames.MercadoPago, (sp, _) => sp.GetRequiredService<MercadoPagoTokenisationProvider>());
        services.AddKeyedTransient<IRawCardTokenisationProvider>(ProviderNames.MercadoPago, (sp, _) => sp.GetRequiredService<MercadoPagoRawCardTokenisationProvider>());

        // Marketplace (collector_id + application_fee splits).
        services.AddHttpClient<MercadoPagoMarketplaceProvider>();
        services.AddTransient<IMarketplaceProvider, MercadoPagoMarketplaceProvider>(sp => sp.GetRequiredService<MercadoPagoMarketplaceProvider>());
        services.AddKeyedTransient<IMarketplaceProvider>(ProviderNames.MercadoPago, (sp, _) => sp.GetRequiredService<MercadoPagoMarketplaceProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
