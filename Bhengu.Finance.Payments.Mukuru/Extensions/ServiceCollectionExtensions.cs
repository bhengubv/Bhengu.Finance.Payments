// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Mukuru.Configuration;
using Bhengu.Finance.Payments.Mukuru.Internals;
using Bhengu.Finance.Payments.Mukuru.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Mukuru.Extensions;

/// <summary>DI registration for the Mukuru provider family — payment + payout + mandate.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the full Mukuru provider family. Reads configuration from
    /// <c>Bhengu:Finance:Payments:Mukuru</c>. Fails fast at startup if required options
    /// (ClientId, ClientSecret) are missing.
    /// </summary>
    public static IServiceCollection AddMukuruPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(MukuruOptions.ConfigSection);
        services.Configure<MukuruOptions>(section);

        var probe = section.Get<MukuruOptions>() ?? new MukuruOptions();
        if (string.IsNullOrWhiteSpace(probe.ClientId))
            throw new ProviderConfigurationException("mukuru", $"{MukuruOptions.ConfigSection}:ClientId is required");
        if (string.IsNullOrWhiteSpace(probe.ClientSecret))
            throw new ProviderConfigurationException("mukuru", $"{MukuruOptions.ConfigSection}:ClientSecret is required");

        services.AddBhenguInMemoryCache();
        services.AddSingleton<MukuruIdempotencyCache>();

        services.AddHttpClient<MukuruPaymentProvider>();
        // MandateProvider reuses MukuruPaymentProvider's HttpClient + token cache via DI.

        services.AddTransient<IPaymentGatewayProvider, MukuruPaymentProvider>(sp =>
            sp.GetRequiredService<MukuruPaymentProvider>());
        services.AddTransient<IPayoutProvider, MukuruPaymentProvider>(sp =>
            sp.GetRequiredService<MukuruPaymentProvider>());
        services.AddTransient<MukuruMandateProvider>();
        services.AddTransient<IMandateProvider>(sp => sp.GetRequiredService<MukuruMandateProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Mukuru,
            (sp, _) => sp.GetRequiredService<MukuruPaymentProvider>());
        services.AddKeyedTransient<IPayoutProvider>(ProviderNames.Mukuru,
            (sp, _) => sp.GetRequiredService<MukuruPaymentProvider>());
        services.AddKeyedTransient<IMandateProvider>(ProviderNames.Mukuru,
            (sp, _) => sp.GetRequiredService<MukuruMandateProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
