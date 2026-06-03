// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Alipay.Configuration;
using Bhengu.Finance.Payments.Alipay.Providers;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Alipay.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Alipay+ Cross-Border provider. Reads configuration from <c>Bhengu:Finance:Payments:Alipay</c>.
    /// Fails fast at startup if required options (ClientId, MerchantPrivateKey) are missing.
    /// </summary>
    public static IServiceCollection AddAlipayPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(AlipayOptions.ConfigSection);
        services.Configure<AlipayOptions>(section);

        var probe = section.Get<AlipayOptions>() ?? new AlipayOptions();
        if (string.IsNullOrWhiteSpace(probe.ClientId))
            throw new ProviderConfigurationException("alipay", $"{AlipayOptions.ConfigSection}:ClientId is required");
        if (string.IsNullOrWhiteSpace(probe.MerchantPrivateKey))
            throw new ProviderConfigurationException("alipay", $"{AlipayOptions.ConfigSection}:MerchantPrivateKey is required");

        services.AddHttpClient<AlipayPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, AlipayPaymentProvider>(sp =>
            sp.GetRequiredService<AlipayPaymentProvider>());
        services.AddTransient<IPayoutProvider, AlipayPaymentProvider>(sp =>
            sp.GetRequiredService<AlipayPaymentProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Alipay, (sp, _) => sp.GetRequiredService<AlipayPaymentProvider>());

        // QR code provider — alipay.trade.precreate / alipay.trade.query (mainland OpenAPI)
        services.AddHttpClient<AlipayQrCodeProvider>();
        services.AddTransient<IQrCodeProvider, AlipayQrCodeProvider>(sp =>
            sp.GetRequiredService<AlipayQrCodeProvider>());
        services.AddKeyedTransient<IQrCodeProvider>(ProviderNames.Alipay, (sp, _) => sp.GetRequiredService<AlipayQrCodeProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
