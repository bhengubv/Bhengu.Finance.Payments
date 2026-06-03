// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.PayFast.Builders;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Bhengu.Finance.Payments.PayFast.Internals;
using Bhengu.Finance.Payments.PayFast.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.PayFast.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the PayFast provider. Reads configuration from <c>Bhengu:Finance:Payments:PayFast</c>.
    /// Fails fast at startup if required options (MerchantId) are missing.
    /// </summary>
    public static IServiceCollection AddPayFastPayments(this IServiceCollection services, IConfiguration configuration)
        => AddPayFastPayments(services, configuration, PayFastOptions.ConfigSection);

    /// <summary>
    /// Register the PayFast provider, binding from a non-default configuration section.
    /// Use this overload when migrating from a legacy configuration layout (e.g. a top-level
    /// <c>"PayFast"</c> section in an existing appsettings.json) without renaming production keys.
    /// </summary>
    public static IServiceCollection AddPayFastPayments(
        this IServiceCollection services,
        IConfiguration configuration,
        string configSectionName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configSectionName);

        var section = configuration.GetSection(configSectionName);
        services.Configure<PayFastOptions>(section);

        var probe = section.Get<PayFastOptions>() ?? new PayFastOptions();
        if (string.IsNullOrWhiteSpace(probe.MerchantId))
            throw new ProviderConfigurationException("payfast", $"{configSectionName}:MerchantId is required");

        services.AddHttpClient<PayFastPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, PayFastPaymentProvider>(sp =>
            sp.GetRequiredService<PayFastPaymentProvider>());
        services.AddTransient<PayFastFormBuilder>();

        // Register as keyed service so consumers can resolve by name: [FromKeyedServices(ProviderNames.PayFast)]
        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.PayFast, (sp, _) => sp.GetRequiredService<PayFastPaymentProvider>());

        // Optional contracts — Subscriptions and Mandates. PayFast natively supports both via the
        // tokenisation / recurring billing endpoints. The in-memory plan cache and amount-limit
        // cache must be singletons to share state across requests.
        services.AddSingleton<PayFastPlanCache>();
        services.AddSingleton<PayFastMandateAmountCache>();

        services.AddHttpClient<PayFastSubscriptionProvider>();
        services.AddTransient<ISubscriptionProvider, PayFastSubscriptionProvider>(sp =>
            sp.GetRequiredService<PayFastSubscriptionProvider>());
        services.AddKeyedTransient<ISubscriptionProvider>(ProviderNames.PayFast, (sp, _) =>
            sp.GetRequiredService<PayFastSubscriptionProvider>());

        services.AddHttpClient<PayFastMandateProvider>();
        services.AddTransient<IMandateProvider, PayFastMandateProvider>(sp =>
            sp.GetRequiredService<PayFastMandateProvider>());
        services.AddKeyedTransient<IMandateProvider>(ProviderNames.PayFast, (sp, _) =>
            sp.GetRequiredService<PayFastMandateProvider>());

        // Eager startup validation — fails the app at startup if config is broken (vs first request)
        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
