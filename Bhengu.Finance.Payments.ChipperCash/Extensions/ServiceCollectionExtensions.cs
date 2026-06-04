// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.ChipperCash.Configuration;
using Bhengu.Finance.Payments.ChipperCash.Providers;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.ChipperCash.Extensions;

/// <summary>DI registration helpers for the Chipper Cash provider family.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Chipper Cash provider family (payment + payout + tokenisation). Reads
    /// configuration from <c>Bhengu:Finance:Payments:ChipperCash</c>. Fails fast at startup if
    /// required options (ApiKey, ApiSecret) are missing. Registers the default in-memory
    /// <see cref="IBhenguDistributedCache"/> so idempotency dedup works without extra wiring;
    /// callers wanting multi-replica safety should subsequently call
    /// <c>services.AddBhenguRedisCache(...)</c>.
    /// </summary>
    public static IServiceCollection AddChipperCashPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ChipperCashOptions.ConfigSection);
        services.Configure<ChipperCashOptions>(section);

        var probe = section.Get<ChipperCashOptions>() ?? new ChipperCashOptions();
        if (string.IsNullOrWhiteSpace(probe.ApiKey))
            throw new ProviderConfigurationException("chippercash", $"{ChipperCashOptions.ConfigSection}:ApiKey is required");
        if (string.IsNullOrWhiteSpace(probe.ApiSecret))
            throw new ProviderConfigurationException("chippercash", $"{ChipperCashOptions.ConfigSection}:ApiSecret is required");

        services.AddBhenguInMemoryCache();

        services.AddHttpClient<ChipperCashPaymentProvider>();
        services.AddHttpClient<ChipperCashTokenisationProvider>();
        services.AddHttpClient<ChipperCashRawCardTokenisationProvider>();

        services.AddTransient<IPaymentGatewayProvider, ChipperCashPaymentProvider>(sp =>
            sp.GetRequiredService<ChipperCashPaymentProvider>());
        services.AddTransient<IPayoutProvider, ChipperCashPaymentProvider>(sp =>
            sp.GetRequiredService<ChipperCashPaymentProvider>());
        services.AddTransient<ITokenisationProvider, ChipperCashTokenisationProvider>(sp =>
            sp.GetRequiredService<ChipperCashTokenisationProvider>());
        services.AddTransient<IRawCardTokenisationProvider, ChipperCashRawCardTokenisationProvider>(sp =>
            sp.GetRequiredService<ChipperCashRawCardTokenisationProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.ChipperCash,
            (sp, _) => sp.GetRequiredService<ChipperCashPaymentProvider>());
        services.AddKeyedTransient<IPayoutProvider>(ProviderNames.ChipperCash,
            (sp, _) => sp.GetRequiredService<ChipperCashPaymentProvider>());
        services.AddKeyedTransient<ITokenisationProvider>(ProviderNames.ChipperCash,
            (sp, _) => sp.GetRequiredService<ChipperCashTokenisationProvider>());
        services.AddKeyedTransient<IRawCardTokenisationProvider>(ProviderNames.ChipperCash,
            (sp, _) => sp.GetRequiredService<ChipperCashRawCardTokenisationProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
