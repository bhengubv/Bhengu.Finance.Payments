// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Mukuru.Configuration;
using Bhengu.Finance.Payments.Mukuru.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Mukuru.Extensions;

/// <summary>DI registration for the MukuruPay provider (delegates to PayFast).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the MukuruPay provider. MukuruPay is a PayFast payment method, so this provider delegates
    /// to PayFast — <b>also call <c>AddPayFastPayments</c></b> so <c>PayFastFormBuilder</c> and
    /// <c>PayFastPaymentProvider</c> resolve. Reads configuration from <c>Bhengu:Finance:Payments:Mukuru</c>.
    /// </summary>
    public static IServiceCollection AddMukuruPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<MukuruOptions>(configuration.GetSection(MukuruOptions.ConfigSection));

        services.AddTransient<MukuruPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, MukuruPaymentProvider>(sp =>
            sp.GetRequiredService<MukuruPaymentProvider>());
        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.Mukuru,
            (sp, _) => sp.GetRequiredService<MukuruPaymentProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
