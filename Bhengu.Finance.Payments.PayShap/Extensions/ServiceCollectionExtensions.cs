using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.PayShap.Client;
using Bhengu.Finance.Payments.PayShap.Configuration;
using Bhengu.Finance.Payments.PayShap.Providers;
using Bhengu.Finance.Payments.PayShap.Services.Implementations;
using Bhengu.Finance.Payments.PayShap.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.PayShap.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register PayShap. Wires the rich <see cref="IPayShapService"/> (for proxy resolution / RTC /
    /// EFT / account verification) AND the <see cref="PayShapPaymentProvider"/> adapter that exposes
    /// PayShap via the generic <see cref="IPaymentGatewayProvider"/> interface for cross-provider tooling.
    /// </summary>
    public static IServiceCollection AddPayShapServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PayShapSettings>(configuration.GetSection(nameof(PayShapSettings)));
        services.AddHttpClient<IPayShapClient, PayShapClient>();
        services.AddScoped<IPayShapService, PayShapService>();

        services.AddTransient<PayShapPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, PayShapPaymentProvider>(sp =>
            sp.GetRequiredService<PayShapPaymentProvider>());

        // Register as keyed service so consumers can resolve by name: [FromKeyedServices(ProviderNames.PayShap)]
        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.PayShap, (sp, _) => sp.GetRequiredService<PayShapPaymentProvider>());

        // QR code provider — deep-link payloads for PayShap QR scan-to-pay
        services.AddTransient<PayShapQrCodeProvider>();
        services.AddTransient<IQrCodeProvider, PayShapQrCodeProvider>(sp =>
            sp.GetRequiredService<PayShapQrCodeProvider>());
        services.AddKeyedTransient<IQrCodeProvider>(ProviderNames.PayShap, (sp, _) => sp.GetRequiredService<PayShapQrCodeProvider>());

        // Eager startup validation — fails the app at startup if config is broken (vs first request)
        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
