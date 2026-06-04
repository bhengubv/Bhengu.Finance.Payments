// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Interswitch.Configuration;
using Bhengu.Finance.Payments.Interswitch.Internals;
using Bhengu.Finance.Payments.Interswitch.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Interswitch.Extensions;

/// <summary>DI registration helpers for the Interswitch provider family.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Interswitch provider. Reads configuration from <c>Bhengu:Finance:Payments:Interswitch</c>.
    /// Fails fast at startup if required options (ClientId, ClientSecret) are missing.
    /// Registers charge + refund + payout on the main provider plus optional tokenisation and
    /// settlement sub-providers keyed by <see cref="ProviderNames.Interswitch"/>.
    /// </summary>
    public static IServiceCollection AddInterswitchPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(InterswitchOptions.ConfigSection);
        services.Configure<InterswitchOptions>(section);

        var probe = section.Get<InterswitchOptions>() ?? new InterswitchOptions();
        if (string.IsNullOrWhiteSpace(probe.ClientId))
            throw new ProviderConfigurationException("interswitch", $"{InterswitchOptions.ConfigSection}:ClientId is required");
        if (string.IsNullOrWhiteSpace(probe.ClientSecret))
            throw new ProviderConfigurationException("interswitch", $"{InterswitchOptions.ConfigSection}:ClientSecret is required");

        services.AddBhenguInMemoryCache();
        services.AddSingleton<InterswitchIdempotencyCache>();

        services.AddHttpClient<InterswitchPaymentProvider>();
        services.AddHttpClient<InterswitchTokenisationProvider>();
        services.AddHttpClient<InterswitchRawCardTokenisationProvider>();
        services.AddHttpClient<InterswitchSettlementProvider>();

        services.AddTransient<IPaymentGatewayProvider, InterswitchPaymentProvider>(sp =>
            sp.GetRequiredService<InterswitchPaymentProvider>());
        services.AddTransient<IPayoutProvider, InterswitchPaymentProvider>(sp =>
            sp.GetRequiredService<InterswitchPaymentProvider>());
        services.AddTransient<ITokenisationProvider, InterswitchTokenisationProvider>(sp =>
            sp.GetRequiredService<InterswitchTokenisationProvider>());
        services.AddTransient<IRawCardTokenisationProvider, InterswitchRawCardTokenisationProvider>(sp =>
            sp.GetRequiredService<InterswitchRawCardTokenisationProvider>());
        services.AddTransient<ISettlementProvider, InterswitchSettlementProvider>(sp =>
            sp.GetRequiredService<InterswitchSettlementProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Interswitch,
            (sp, _) => sp.GetRequiredService<InterswitchPaymentProvider>());
        services.AddKeyedTransient<IPayoutProvider>(ProviderNames.Interswitch,
            (sp, _) => sp.GetRequiredService<InterswitchPaymentProvider>());
        services.AddKeyedTransient<ITokenisationProvider>(ProviderNames.Interswitch,
            (sp, _) => sp.GetRequiredService<InterswitchTokenisationProvider>());
        services.AddKeyedTransient<IRawCardTokenisationProvider>(ProviderNames.Interswitch,
            (sp, _) => sp.GetRequiredService<InterswitchRawCardTokenisationProvider>());
        services.AddKeyedTransient<ISettlementProvider>(ProviderNames.Interswitch,
            (sp, _) => sp.GetRequiredService<InterswitchSettlementProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
