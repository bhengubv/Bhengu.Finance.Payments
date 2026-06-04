// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.OPay.Configuration;
using Bhengu.Finance.Payments.OPay.Internals;
using Bhengu.Finance.Payments.OPay.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.OPay.Extensions;

/// <summary>DI registration helpers for the OPay provider family.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the OPay provider. Reads configuration from <c>Bhengu:Finance:Payments:OPay</c>.
    /// Fails fast at startup if required options (PublicKey, SecretKey, MerchantId) are missing.
    /// Registers charge + refund + payout + tokenisation (saved bank accounts) + settlement,
    /// keyed by <see cref="ProviderNames.OPay"/>.
    /// </summary>
    public static IServiceCollection AddOPayPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(OPayOptions.ConfigSection);
        services.Configure<OPayOptions>(section);

        var probe = section.Get<OPayOptions>() ?? new OPayOptions();
        if (string.IsNullOrWhiteSpace(probe.PublicKey))
            throw new ProviderConfigurationException("opay", $"{OPayOptions.ConfigSection}:PublicKey is required");
        if (string.IsNullOrWhiteSpace(probe.SecretKey))
            throw new ProviderConfigurationException("opay", $"{OPayOptions.ConfigSection}:SecretKey is required");
        if (string.IsNullOrWhiteSpace(probe.MerchantId))
            throw new ProviderConfigurationException("opay", $"{OPayOptions.ConfigSection}:MerchantId is required");

        services.AddBhenguInMemoryCache();
        services.AddSingleton<OPayIdempotencyCache>();

        services.AddHttpClient<OPayPaymentProvider>();
        services.AddHttpClient<OPayTokenisationProvider>();
        services.AddHttpClient<OPaySettlementProvider>();

        services.AddTransient<IPaymentGatewayProvider, OPayPaymentProvider>(sp =>
            sp.GetRequiredService<OPayPaymentProvider>());
        services.AddTransient<IPayoutProvider, OPayPaymentProvider>(sp =>
            sp.GetRequiredService<OPayPaymentProvider>());
        services.AddTransient<ITokenisationProvider, OPayTokenisationProvider>(sp =>
            sp.GetRequiredService<OPayTokenisationProvider>());
        services.AddTransient<ISettlementProvider, OPaySettlementProvider>(sp =>
            sp.GetRequiredService<OPaySettlementProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.OPay,
            (sp, _) => sp.GetRequiredService<OPayPaymentProvider>());
        services.AddKeyedTransient<IPayoutProvider>(ProviderNames.OPay,
            (sp, _) => sp.GetRequiredService<OPayPaymentProvider>());
        services.AddKeyedTransient<ITokenisationProvider>(ProviderNames.OPay,
            (sp, _) => sp.GetRequiredService<OPayTokenisationProvider>());
        services.AddKeyedTransient<ISettlementProvider>(ProviderNames.OPay,
            (sp, _) => sp.GetRequiredService<OPaySettlementProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
