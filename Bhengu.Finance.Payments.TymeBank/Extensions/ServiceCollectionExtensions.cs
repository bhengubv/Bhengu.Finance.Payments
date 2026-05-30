// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.TymeBank.Configuration;
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

        services.AddHttpClient<TymeBankPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, TymeBankPaymentProvider>(sp =>
            sp.GetRequiredService<TymeBankPaymentProvider>());
        services.AddTransient<IPayoutProvider, TymeBankPaymentProvider>(sp =>
            sp.GetRequiredService<TymeBankPaymentProvider>());

        return services;
    }
}
