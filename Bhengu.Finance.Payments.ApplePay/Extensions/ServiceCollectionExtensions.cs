// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.ApplePay.Configuration;
using Bhengu.Finance.Payments.ApplePay.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.ApplePay.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Apple Pay provider. Reads <c>Bhengu:Finance:Payments:ApplePay</c>.
    /// The provider validates the PKPaymentToken shape, tags the request, and forwards the
    /// charge to the registered downstream processor named in
    /// <see cref="ApplePayOptions.DownstreamProcessor"/>. Register the downstream processor first.
    /// </summary>
    public static IServiceCollection AddApplePayPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ApplePayOptions.ConfigSection);
        services.Configure<ApplePayOptions>(section);

        var probe = section.Get<ApplePayOptions>() ?? new ApplePayOptions();
        if (string.IsNullOrWhiteSpace(probe.MerchantId))
            throw new ProviderConfigurationException("applepay", $"{ApplePayOptions.ConfigSection}:MerchantId is required");
        if (string.IsNullOrWhiteSpace(probe.DownstreamProcessor))
            throw new ProviderConfigurationException("applepay", $"{ApplePayOptions.ConfigSection}:DownstreamProcessor is required");

        services.AddTransient<ApplePayPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, ApplePayPaymentProvider>(sp =>
            sp.GetRequiredService<ApplePayPaymentProvider>());
        return services;
    }
}
