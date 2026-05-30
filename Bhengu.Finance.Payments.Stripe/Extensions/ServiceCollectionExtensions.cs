// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Stripe.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Stripe provider. Reads configuration from <c>Bhengu:Finance:Payments:Stripe</c>.
    /// Fails fast at startup if required options (SecretKey) are missing.
    /// </summary>
    public static IServiceCollection AddStripePayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(StripeOptions.ConfigSection);
        services.Configure<StripeOptions>(section);

        var probe = section.Get<StripeOptions>() ?? new StripeOptions();
        if (string.IsNullOrWhiteSpace(probe.SecretKey))
            throw new ProviderConfigurationException("stripe", $"{StripeOptions.ConfigSection}:SecretKey is required");

        services.AddHttpClient<StripePaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, StripePaymentProvider>(sp =>
            sp.GetRequiredService<StripePaymentProvider>());
        services.AddTransient<IPayoutProvider, StripePaymentProvider>(sp =>
            sp.GetRequiredService<StripePaymentProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Stripe, (sp, _) => sp.GetRequiredService<StripePaymentProvider>());
        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
