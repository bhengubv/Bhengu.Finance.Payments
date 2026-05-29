// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Google.Configuration;
using Bhengu.Finance.Payments.Google.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Google.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Google Pay provider scaffold. Reads <c>Bhengu:Finance:Payments:GooglePay</c>.
    /// The scaffold throws when called — see GooglePayPaymentProvider for completion steps.
    /// </summary>
    public static IServiceCollection AddGooglePayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<GooglePayOptions>(configuration.GetSection(GooglePayOptions.ConfigSection));
        services.AddTransient<GooglePayPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, GooglePayPaymentProvider>(sp =>
            sp.GetRequiredService<GooglePayPaymentProvider>());
        return services;
    }
}
