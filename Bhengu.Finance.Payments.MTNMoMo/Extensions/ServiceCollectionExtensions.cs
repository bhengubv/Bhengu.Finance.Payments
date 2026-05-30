// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.MTNMoMo.Configuration;
using Bhengu.Finance.Payments.MTNMoMo.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.MTNMoMo.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the MTN Mobile Money (MoMo) provider. Reads configuration from <c>Bhengu:Finance:Payments:MTNMoMo</c>.
    /// Fails fast at startup if required options (SubscriptionKey, ApiUserId, ApiKey, TargetEnvironment) are missing.
    /// </summary>
    public static IServiceCollection AddMTNMoMoPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(MTNMoMoOptions.ConfigSection);
        services.Configure<MTNMoMoOptions>(section);

        var probe = section.Get<MTNMoMoOptions>() ?? new MTNMoMoOptions();
        if (string.IsNullOrWhiteSpace(probe.SubscriptionKey))
            throw new ProviderConfigurationException("mtnmomo", $"{MTNMoMoOptions.ConfigSection}:SubscriptionKey is required");
        if (string.IsNullOrWhiteSpace(probe.ApiUserId))
            throw new ProviderConfigurationException("mtnmomo", $"{MTNMoMoOptions.ConfigSection}:ApiUserId is required");
        if (string.IsNullOrWhiteSpace(probe.ApiKey))
            throw new ProviderConfigurationException("mtnmomo", $"{MTNMoMoOptions.ConfigSection}:ApiKey is required");
        if (string.IsNullOrWhiteSpace(probe.TargetEnvironment))
            throw new ProviderConfigurationException("mtnmomo", $"{MTNMoMoOptions.ConfigSection}:TargetEnvironment is required");

        services.AddHttpClient<MTNMoMoPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, MTNMoMoPaymentProvider>(sp =>
            sp.GetRequiredService<MTNMoMoPaymentProvider>());
        services.AddTransient<IPayoutProvider, MTNMoMoPaymentProvider>(sp =>
            sp.GetRequiredService<MTNMoMoPaymentProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.MTNMoMo, (sp, _) => sp.GetRequiredService<MTNMoMoPaymentProvider>());
        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
