// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Google.Configuration;
using Bhengu.Finance.Payments.Google.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Google.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Google Pay provider. Reads <c>Bhengu:Finance:Payments:GooglePay</c>.
    /// The provider validates the Google Pay token shape, tags the request, and forwards the
    /// charge to the registered downstream processor named in
    /// <see cref="GooglePayOptions.DownstreamProcessor"/>. Register the downstream processor first.
    /// </summary>
    public static IServiceCollection AddGooglePayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(GooglePayOptions.ConfigSection);
        services.Configure<GooglePayOptions>(section);

        var probe = section.Get<GooglePayOptions>() ?? new GooglePayOptions();
        if (string.IsNullOrWhiteSpace(probe.MerchantId))
            throw new ProviderConfigurationException("googlepay", $"{GooglePayOptions.ConfigSection}:MerchantId is required");
        if (string.IsNullOrWhiteSpace(probe.DownstreamProcessor))
            throw new ProviderConfigurationException("googlepay", $"{GooglePayOptions.ConfigSection}:DownstreamProcessor is required");

        services.AddTransient<GooglePayPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, GooglePayPaymentProvider>(sp =>
            sp.GetRequiredService<GooglePayPaymentProvider>());
        return services;
    }
}
