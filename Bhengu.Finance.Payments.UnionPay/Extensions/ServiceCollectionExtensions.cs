// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.UnionPay.Configuration;
using Bhengu.Finance.Payments.UnionPay.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.UnionPay.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the China UnionPay (UPOP) provider. Reads configuration from <c>Bhengu:Finance:Payments:UnionPay</c>.
    /// Fails fast at startup if required options (MerId, CertId, SignCertPrivateKey) are missing.
    /// </summary>
    public static IServiceCollection AddUnionPayPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(UnionPayOptions.ConfigSection);
        services.Configure<UnionPayOptions>(section);

        var probe = section.Get<UnionPayOptions>() ?? new UnionPayOptions();
        if (string.IsNullOrWhiteSpace(probe.MerId))
            throw new ProviderConfigurationException("unionpay", $"{UnionPayOptions.ConfigSection}:MerId is required");
        if (string.IsNullOrWhiteSpace(probe.CertId))
            throw new ProviderConfigurationException("unionpay", $"{UnionPayOptions.ConfigSection}:CertId is required");
        if (string.IsNullOrWhiteSpace(probe.SignCertPrivateKey))
            throw new ProviderConfigurationException("unionpay", $"{UnionPayOptions.ConfigSection}:SignCertPrivateKey is required");

        services.AddHttpClient<UnionPayPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, UnionPayPaymentProvider>(sp =>
            sp.GetRequiredService<UnionPayPaymentProvider>());

        return services;
    }
}
