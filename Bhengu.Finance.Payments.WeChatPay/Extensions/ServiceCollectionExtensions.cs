// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.WeChatPay.Configuration;
using Bhengu.Finance.Payments.WeChatPay.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.WeChatPay.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the WeChat Pay v3 provider. Reads configuration from <c>Bhengu:Finance:Payments:WeChatPay</c>.
    /// Fails fast at startup if required options (AppId, MerchantId, MerchantCertSerialNo, MerchantPrivateKey) are missing.
    /// </summary>
    public static IServiceCollection AddWeChatPayPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(WeChatPayOptions.ConfigSection);
        services.Configure<WeChatPayOptions>(section);

        var probe = section.Get<WeChatPayOptions>() ?? new WeChatPayOptions();
        if (string.IsNullOrWhiteSpace(probe.AppId))
            throw new ProviderConfigurationException("wechatpay", $"{WeChatPayOptions.ConfigSection}:AppId is required");
        if (string.IsNullOrWhiteSpace(probe.MerchantId))
            throw new ProviderConfigurationException("wechatpay", $"{WeChatPayOptions.ConfigSection}:MerchantId is required");
        if (string.IsNullOrWhiteSpace(probe.MerchantCertSerialNo))
            throw new ProviderConfigurationException("wechatpay", $"{WeChatPayOptions.ConfigSection}:MerchantCertSerialNo is required");
        if (string.IsNullOrWhiteSpace(probe.MerchantPrivateKey))
            throw new ProviderConfigurationException("wechatpay", $"{WeChatPayOptions.ConfigSection}:MerchantPrivateKey is required");

        services.AddHttpClient<WeChatPayPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, WeChatPayPaymentProvider>(sp =>
            sp.GetRequiredService<WeChatPayPaymentProvider>());
        services.AddTransient<IPayoutProvider, WeChatPayPaymentProvider>(sp =>
            sp.GetRequiredService<WeChatPayPaymentProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.WeChatPay, (sp, _) => sp.GetRequiredService<WeChatPayPaymentProvider>());
        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
