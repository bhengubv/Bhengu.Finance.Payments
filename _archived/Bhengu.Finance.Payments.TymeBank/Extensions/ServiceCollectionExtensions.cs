// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.TymeBank.Configuration;
using Bhengu.Finance.Payments.TymeBank.Internals;
using Bhengu.Finance.Payments.TymeBank.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.TymeBank.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the TymeBank provider. Reads configuration from <c>Bhengu:Finance:Payments:TymeBank</c>.
    /// Fails fast at startup if required options (ClientId, ClientSecret) are missing.
    /// </summary>
    public static IServiceCollection AddTymeBankPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(TymeBankOptions.ConfigSection);
        services.Configure<TymeBankOptions>(section);

        var probe = section.Get<TymeBankOptions>() ?? new TymeBankOptions();
        if (string.IsNullOrWhiteSpace(probe.ClientId))
            throw new ProviderConfigurationException("tymebank", $"{TymeBankOptions.ConfigSection}:ClientId is required");
        if (string.IsNullOrWhiteSpace(probe.ClientSecret))
            throw new ProviderConfigurationException("tymebank", $"{TymeBankOptions.ConfigSection}:ClientSecret is required");

        services.AddBhenguInMemoryCache();
        services.AddSingleton<TymeBankOAuthCache>();
        services.AddHttpClient<TymeBankPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, TymeBankPaymentProvider>(sp =>
            sp.GetRequiredService<TymeBankPaymentProvider>());
        services.AddTransient<IPayoutProvider, TymeBankPaymentProvider>(sp =>
            sp.GetRequiredService<TymeBankPaymentProvider>());

        // Register as keyed service so consumers can resolve by name: [FromKeyedServices(ProviderNames.TymeBank)]
        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.TymeBank, (sp, _) => sp.GetRequiredService<TymeBankPaymentProvider>());

        // Debit-order mandate provider — uses the same OAuth2 credentials as the payment provider.
        services.AddHttpClient<TymeBankMandateProvider>();
        services.AddTransient<IMandateProvider, TymeBankMandateProvider>(sp => sp.GetRequiredService<TymeBankMandateProvider>());
        services.AddKeyedTransient<IMandateProvider>(ProviderNames.TymeBank, (sp, _) => sp.GetRequiredService<TymeBankMandateProvider>());

        // Eager startup validation — fails the app at startup if config is broken (vs first request)
        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
