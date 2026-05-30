// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Bhengu.Finance.Payments.Razorpay.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Razorpay.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Razorpay provider. Reads configuration from <c>Bhengu:Finance:Payments:Razorpay</c>.
    /// Fails fast at startup if required options (KeyId, KeySecret) are missing.
    /// </summary>
    public static IServiceCollection AddRazorpayPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(RazorpayOptions.ConfigSection);
        services.Configure<RazorpayOptions>(section);

        var probe = section.Get<RazorpayOptions>() ?? new RazorpayOptions();
        if (string.IsNullOrWhiteSpace(probe.KeyId))
            throw new ProviderConfigurationException("razorpay", $"{RazorpayOptions.ConfigSection}:KeyId is required");
        if (string.IsNullOrWhiteSpace(probe.KeySecret))
            throw new ProviderConfigurationException("razorpay", $"{RazorpayOptions.ConfigSection}:KeySecret is required");

        services.AddHttpClient<RazorpayPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, RazorpayPaymentProvider>(sp =>
            sp.GetRequiredService<RazorpayPaymentProvider>());
        services.AddTransient<IPayoutProvider, RazorpayPaymentProvider>(sp =>
            sp.GetRequiredService<RazorpayPaymentProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Razorpay, (sp, _) => sp.GetRequiredService<RazorpayPaymentProvider>());
        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
