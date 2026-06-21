// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.EcoCash.Configuration;
using Bhengu.Finance.Payments.EcoCash.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.EcoCash.Extensions;

/// <summary>DI registration helpers for the EcoCash (Zimbabwe) provider.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the EcoCash (Zimbabwe) provider against the public EcoCash Open API. Implements
    /// C2B charge + refund (no documented B2C/payout, so <c>IPayoutProvider</c> is not registered).
    /// Reads configuration from <c>Bhengu:Finance:Payments:EcoCash</c> and fails fast at startup if
    /// the required <c>ApiKey</c> is missing.
    /// </summary>
    public static IServiceCollection AddEcoCashPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(EcoCashOptions.ConfigSection);
        services.Configure<EcoCashOptions>(section);

        var probe = section.Get<EcoCashOptions>() ?? new EcoCashOptions();
        if (string.IsNullOrWhiteSpace(probe.ApiKey))
            throw new ProviderConfigurationException("ecocash", $"{EcoCashOptions.ConfigSection}:ApiKey is required");

        services.AddBhenguInMemoryCache();
        services.AddHttpClient<EcoCashPaymentProvider>();

        services.AddTransient<IPaymentGatewayProvider, EcoCashPaymentProvider>(sp =>
            sp.GetRequiredService<EcoCashPaymentProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.EcoCash,
            (sp, _) => sp.GetRequiredService<EcoCashPaymentProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
