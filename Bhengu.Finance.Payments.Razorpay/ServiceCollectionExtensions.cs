// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Bhengu.Finance.Payments.Razorpay.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Razorpay;

/// <summary>
/// DI extensions that register every Razorpay capability provider in one call. Use this when you
/// want the full Razorpay feature set (vault, plans, subscriptions, settlements, mandates,
/// Razorpay Route marketplace, RazorpayX payouts, disputes, typed webhooks, idempotency).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the full Razorpay provider family. Reads configuration from
    /// <c>Bhengu:Finance:Payments:Razorpay</c>. Fails fast at startup if required options
    /// (KeyId, KeySecret) are missing.
    /// </summary>
    /// <remarks>
    /// Each capability is registered both as its interface (so callers can inject
    /// <c>ITokenisationProvider</c>, <c>ISubscriptionProvider</c>, etc.) and as a keyed service
    /// against <see cref="ProviderNames.Razorpay"/> for multi-provider DI scenarios.
    /// </remarks>
    public static IServiceCollection AddBhenguRazorpayPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(RazorpayOptions.ConfigSection);
        services.Configure<RazorpayOptions>(section);

        var probe = section.Get<RazorpayOptions>() ?? new RazorpayOptions();
        if (string.IsNullOrWhiteSpace(probe.KeyId))
            throw new ProviderConfigurationException(ProviderNames.Razorpay, $"{RazorpayOptions.ConfigSection}:KeyId is required");
        if (string.IsNullOrWhiteSpace(probe.KeySecret))
            throw new ProviderConfigurationException(ProviderNames.Razorpay, $"{RazorpayOptions.ConfigSection}:KeySecret is required");

        // The core payment provider still owns Charge / Refund / legacy Payout / Webhook verify.
        services.AddHttpClient<RazorpayPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, RazorpayPaymentProvider>(sp =>
            sp.GetRequiredService<RazorpayPaymentProvider>());
        services.AddTransient<IPayoutProvider, RazorpayPaymentProvider>(sp =>
            sp.GetRequiredService<RazorpayPaymentProvider>());
        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Razorpay, (sp, _) =>
            sp.GetRequiredService<RazorpayPaymentProvider>());

        // New single-responsibility providers — one HTTP client each so the SDK can wire per-provider
        // delegating handlers (telemetry, retry, rate-limit) without polluting the others.
        services.AddHttpClient<RazorpayTokenisationProvider>();
        services.AddTransient<ITokenisationProvider, RazorpayTokenisationProvider>(sp => sp.GetRequiredService<RazorpayTokenisationProvider>());
        services.AddKeyedTransient<ITokenisationProvider>(ProviderNames.Razorpay, (sp, _) => sp.GetRequiredService<RazorpayTokenisationProvider>());

        services.AddHttpClient<RazorpaySubscriptionProvider>();
        services.AddTransient<ISubscriptionProvider, RazorpaySubscriptionProvider>(sp => sp.GetRequiredService<RazorpaySubscriptionProvider>());
        services.AddKeyedTransient<ISubscriptionProvider>(ProviderNames.Razorpay, (sp, _) => sp.GetRequiredService<RazorpaySubscriptionProvider>());

        services.AddHttpClient<RazorpaySettlementProvider>();
        services.AddTransient<ISettlementProvider, RazorpaySettlementProvider>(sp => sp.GetRequiredService<RazorpaySettlementProvider>());
        services.AddKeyedTransient<ISettlementProvider>(ProviderNames.Razorpay, (sp, _) => sp.GetRequiredService<RazorpaySettlementProvider>());

        services.AddHttpClient<RazorpayMandateProvider>();
        services.AddTransient<IMandateProvider, RazorpayMandateProvider>(sp => sp.GetRequiredService<RazorpayMandateProvider>());
        services.AddKeyedTransient<IMandateProvider>(ProviderNames.Razorpay, (sp, _) => sp.GetRequiredService<RazorpayMandateProvider>());

        // Marketplace provider holds an in-process split cache so it must be singleton, not transient.
        services.AddHttpClient<RazorpayMarketplaceProvider>();
        services.AddSingleton<RazorpayMarketplaceProvider>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return ActivatorUtilities.CreateInstance<RazorpayMarketplaceProvider>(sp, factory.CreateClient(nameof(RazorpayMarketplaceProvider)));
        });
        services.AddTransient<IMarketplaceProvider>(sp => sp.GetRequiredService<RazorpayMarketplaceProvider>());
        services.AddKeyedTransient<IMarketplaceProvider>(ProviderNames.Razorpay, (sp, _) => sp.GetRequiredService<RazorpayMarketplaceProvider>());

        services.AddHttpClient<RazorpayPayoutProvider>();
        // Keyed payout — there are TWO IPayoutProvider implementations bound to razorpay (legacy on
        // RazorpayPaymentProvider, new on RazorpayPayoutProvider). The keyed lookup resolves the new
        // single-responsibility one; legacy callers using bare IPayoutProvider still get the first
        // registered transient (also new), but old DI graphs continue to work via RazorpayPaymentProvider.
        services.AddKeyedTransient<IPayoutProvider>(ProviderNames.Razorpay, (sp, _) => sp.GetRequiredService<RazorpayPayoutProvider>());

        services.AddHttpClient<RazorpayDisputeProvider>();
        services.AddTransient<IDisputeProvider, RazorpayDisputeProvider>(sp => sp.GetRequiredService<RazorpayDisputeProvider>());
        services.AddKeyedTransient<IDisputeProvider>(ProviderNames.Razorpay, (sp, _) => sp.GetRequiredService<RazorpayDisputeProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
