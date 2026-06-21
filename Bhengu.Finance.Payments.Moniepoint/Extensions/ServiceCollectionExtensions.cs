// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Moniepoint.Configuration;
using Bhengu.Finance.Payments.Moniepoint.Internals;
using Bhengu.Finance.Payments.Moniepoint.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Moniepoint.Extensions;

/// <summary>DI registration helpers for the Moniepoint (Monnify) provider.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Moniepoint provider (integrating Monnify). Reads configuration from
    /// <c>Bhengu:Finance:Payments:Moniepoint</c> and fails fast at startup if required options
    /// (ApiKey, SecretKey, ContractCode) are missing. Registers charge + payout, keyed by
    /// <see cref="ProviderNames.Moniepoint"/>.
    /// </summary>
    public static IServiceCollection AddMoniepointPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(MoniepointOptions.ConfigSection);
        services.Configure<MoniepointOptions>(section);

        var probe = section.Get<MoniepointOptions>() ?? new MoniepointOptions();
        if (string.IsNullOrWhiteSpace(probe.ApiKey))
            throw new ProviderConfigurationException("moniepoint", $"{MoniepointOptions.ConfigSection}:ApiKey is required");
        if (string.IsNullOrWhiteSpace(probe.SecretKey))
            throw new ProviderConfigurationException("moniepoint", $"{MoniepointOptions.ConfigSection}:SecretKey is required");
        if (string.IsNullOrWhiteSpace(probe.ContractCode))
            throw new ProviderConfigurationException("moniepoint", $"{MoniepointOptions.ConfigSection}:ContractCode is required");

        services.AddBhenguInMemoryCache();
        services.AddSingleton<MoniepointIdempotencyCache>();

        services.AddHttpClient<MoniepointPaymentProvider>();

        services.AddTransient<IPaymentGatewayProvider, MoniepointPaymentProvider>(sp =>
            sp.GetRequiredService<MoniepointPaymentProvider>());
        services.AddTransient<IPayoutProvider, MoniepointPaymentProvider>(sp =>
            sp.GetRequiredService<MoniepointPaymentProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Moniepoint,
            (sp, _) => sp.GetRequiredService<MoniepointPaymentProvider>());
        services.AddKeyedTransient<IPayoutProvider>(ProviderNames.Moniepoint,
            (sp, _) => sp.GetRequiredService<MoniepointPaymentProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
