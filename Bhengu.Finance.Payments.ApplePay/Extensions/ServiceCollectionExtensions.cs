// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.ApplePay.Configuration;
using Bhengu.Finance.Payments.ApplePay.Providers;
using Bhengu.Finance.Payments.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.ApplePay.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Apple Pay provider scaffold. Reads <c>Bhengu:Finance:Payments:ApplePay</c>.
    /// The scaffold throws when called — see ApplePayPaymentProvider for completion steps.
    /// </summary>
    public static IServiceCollection AddApplePayPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<ApplePayOptions>(configuration.GetSection(ApplePayOptions.ConfigSection));
        services.AddTransient<ApplePayPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, ApplePayPaymentProvider>(sp =>
            sp.GetRequiredService<ApplePayPaymentProvider>());
        return services;
    }
}
