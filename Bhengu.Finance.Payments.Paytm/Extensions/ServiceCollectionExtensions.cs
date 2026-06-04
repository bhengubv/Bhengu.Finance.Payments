// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Paytm.Configuration;
using Bhengu.Finance.Payments.Paytm.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Paytm.Extensions;

/// <summary>
/// DI registration helpers for the Paytm provider family.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Paytm payment + payout + tokenisation + subscription + QR + settlement
    /// providers. Reads configuration from <c>Bhengu:Finance:Payments:Paytm</c>. Fails fast at
    /// startup if required options (MerchantId, MerchantKey) are missing.
    /// </summary>
    public static IServiceCollection AddPaytmPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(PaytmOptions.ConfigSection);
        services.Configure<PaytmOptions>(section);

        var probe = section.Get<PaytmOptions>() ?? new PaytmOptions();
        if (string.IsNullOrWhiteSpace(probe.MerchantId))
            throw new ProviderConfigurationException("paytm", $"{PaytmOptions.ConfigSection}:MerchantId is required");
        if (string.IsNullOrWhiteSpace(probe.MerchantKey))
            throw new ProviderConfigurationException("paytm", $"{PaytmOptions.ConfigSection}:MerchantKey is required");

        services.AddBhenguInMemoryCache();

        services.AddHttpClient<PaytmPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, PaytmPaymentProvider>(sp =>
            sp.GetRequiredService<PaytmPaymentProvider>());
        services.AddTransient<IPayoutProvider, PaytmPaymentProvider>(sp =>
            sp.GetRequiredService<PaytmPaymentProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Paytm, (sp, _) => sp.GetRequiredService<PaytmPaymentProvider>());
        services.AddKeyedTransient<IPayoutProvider>(ProviderNames.Paytm, (sp, _) => sp.GetRequiredService<PaytmPaymentProvider>());

        // Optional contracts.
        services.AddHttpClient<PaytmTokenisationProvider>();
        services.AddHttpClient<PaytmRawCardTokenisationProvider>();
        services.AddHttpClient<PaytmSubscriptionProvider>();
        services.AddHttpClient<PaytmQrCodeProvider>();
        services.AddHttpClient<PaytmSettlementProvider>();

        services.AddTransient<ITokenisationProvider>(sp => sp.GetRequiredService<PaytmTokenisationProvider>());
        services.AddTransient<IRawCardTokenisationProvider>(sp => sp.GetRequiredService<PaytmRawCardTokenisationProvider>());
        services.AddTransient<ISubscriptionProvider>(sp => sp.GetRequiredService<PaytmSubscriptionProvider>());
        services.AddTransient<IQrCodeProvider>(sp => sp.GetRequiredService<PaytmQrCodeProvider>());
        services.AddTransient<ISettlementProvider>(sp => sp.GetRequiredService<PaytmSettlementProvider>());

        services.AddKeyedTransient<ITokenisationProvider>(ProviderNames.Paytm, (sp, _) => sp.GetRequiredService<PaytmTokenisationProvider>());
        services.AddKeyedTransient<IRawCardTokenisationProvider>(ProviderNames.Paytm, (sp, _) => sp.GetRequiredService<PaytmRawCardTokenisationProvider>());
        services.AddKeyedTransient<ISubscriptionProvider>(ProviderNames.Paytm, (sp, _) => sp.GetRequiredService<PaytmSubscriptionProvider>());
        services.AddKeyedTransient<IQrCodeProvider>(ProviderNames.Paytm, (sp, _) => sp.GetRequiredService<PaytmQrCodeProvider>());
        services.AddKeyedTransient<ISettlementProvider>(ProviderNames.Paytm, (sp, _) => sp.GetRequiredService<PaytmSettlementProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
