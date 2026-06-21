// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Onafriq.Configuration;
using Bhengu.Finance.Payments.Onafriq.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Onafriq.Extensions;

/// <summary>DI registration helpers for the Onafriq (formerly MFS Africa) provider.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Onafriq (formerly MFS Africa) provider. Reads configuration from
    /// <c>Bhengu:Finance:Payments:Onafriq</c>. Fails fast at startup if the required
    /// <see cref="OnafriqOptions.ApiKey"/> is missing.
    /// </summary>
    public static IServiceCollection AddOnafriqPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(OnafriqOptions.ConfigSection);
        services.Configure<OnafriqOptions>(section);

        var probe = section.Get<OnafriqOptions>() ?? new OnafriqOptions();
        if (string.IsNullOrWhiteSpace(probe.ApiKey))
            throw new ProviderConfigurationException("onafriq", $"{OnafriqOptions.ConfigSection}:ApiKey is required");

        services.AddBhenguInMemoryCache();
        services.AddHttpClient<OnafriqPaymentProvider>();

        services.AddTransient<IPaymentGatewayProvider, OnafriqPaymentProvider>(sp =>
            sp.GetRequiredService<OnafriqPaymentProvider>());
        services.AddTransient<IPayoutProvider, OnafriqPaymentProvider>(sp =>
            sp.GetRequiredService<OnafriqPaymentProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Onafriq,
            (sp, _) => sp.GetRequiredService<OnafriqPaymentProvider>());
        services.AddKeyedTransient<IPayoutProvider>(ProviderNames.Onafriq,
            (sp, _) => sp.GetRequiredService<OnafriqPaymentProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
