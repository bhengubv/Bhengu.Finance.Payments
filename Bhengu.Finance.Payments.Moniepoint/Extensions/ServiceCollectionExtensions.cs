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

/// <summary>DI registration helpers for the Moniepoint provider family.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Moniepoint provider. Reads configuration from <c>Bhengu:Finance:Payments:Moniepoint</c>.
    /// Fails fast at startup if required options (ApiKey) are missing. Registers charge + refund +
    /// payout + settlement, keyed by <see cref="ProviderNames.Moniepoint"/>.
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

        services.AddBhenguInMemoryCache();
        services.AddSingleton<MoniepointIdempotencyCache>();

        services.AddHttpClient<MoniepointPaymentProvider>();
        services.AddHttpClient<MoniepointSettlementProvider>();

        services.AddTransient<IPaymentGatewayProvider, MoniepointPaymentProvider>(sp =>
            sp.GetRequiredService<MoniepointPaymentProvider>());
        services.AddTransient<IPayoutProvider, MoniepointPaymentProvider>(sp =>
            sp.GetRequiredService<MoniepointPaymentProvider>());
        services.AddTransient<ISettlementProvider, MoniepointSettlementProvider>(sp =>
            sp.GetRequiredService<MoniepointSettlementProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Moniepoint,
            (sp, _) => sp.GetRequiredService<MoniepointPaymentProvider>());
        services.AddKeyedTransient<IPayoutProvider>(ProviderNames.Moniepoint,
            (sp, _) => sp.GetRequiredService<MoniepointPaymentProvider>());
        services.AddKeyedTransient<ISettlementProvider>(ProviderNames.Moniepoint,
            (sp, _) => sp.GetRequiredService<MoniepointSettlementProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
