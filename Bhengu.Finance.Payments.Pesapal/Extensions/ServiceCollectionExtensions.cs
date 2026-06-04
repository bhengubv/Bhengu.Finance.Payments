// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Pesapal.Configuration;
using Bhengu.Finance.Payments.Pesapal.Internals;
using Bhengu.Finance.Payments.Pesapal.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Pesapal.Extensions;

/// <summary>DI registration helpers for the Pesapal provider family.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Pesapal provider family. Reads configuration from <c>Bhengu:Finance:Payments:Pesapal</c>.
    /// Fails fast at startup if required options (ConsumerKey, ConsumerSecret) are missing.
    /// </summary>
    public static IServiceCollection AddPesapalPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(PesapalOptions.ConfigSection);
        services.Configure<PesapalOptions>(section);

        var probe = section.Get<PesapalOptions>() ?? new PesapalOptions();
        if (string.IsNullOrWhiteSpace(probe.ConsumerKey))
            throw new ProviderConfigurationException("pesapal", $"{PesapalOptions.ConfigSection}:ConsumerKey is required");
        if (string.IsNullOrWhiteSpace(probe.ConsumerSecret))
            throw new ProviderConfigurationException("pesapal", $"{PesapalOptions.ConfigSection}:ConsumerSecret is required");

        services.AddBhenguInMemoryCache();
        services.AddSingleton<PesapalTokenCache>();

        services.AddHttpClient<PesapalPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, PesapalPaymentProvider>(sp =>
            sp.GetRequiredService<PesapalPaymentProvider>());
        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Pesapal, (sp, _) => sp.GetRequiredService<PesapalPaymentProvider>());

        services.AddHttpClient<PesapalSubscriptionProvider>();
        services.AddTransient<ISubscriptionProvider, PesapalSubscriptionProvider>(sp =>
            sp.GetRequiredService<PesapalSubscriptionProvider>());
        services.AddKeyedTransient<ISubscriptionProvider>(ProviderNames.Pesapal, (sp, _) => sp.GetRequiredService<PesapalSubscriptionProvider>());

        services.AddHttpClient<PesapalSettlementProvider>();
        services.AddTransient<ISettlementProvider, PesapalSettlementProvider>(sp =>
            sp.GetRequiredService<PesapalSettlementProvider>());
        services.AddKeyedTransient<ISettlementProvider>(ProviderNames.Pesapal, (sp, _) => sp.GetRequiredService<PesapalSettlementProvider>());

        services.AddBhenguPaymentStartupValidation();
        return services;
    }
}
