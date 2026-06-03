// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.PagSeguro.Configuration;
using Bhengu.Finance.Payments.PagSeguro.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.PagSeguro.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the PagSeguro (PagBank) provider. Reads configuration from <c>Bhengu:Finance:Payments:PagSeguro</c>.
    /// Fails fast at startup if required options (ApiToken) are missing.
    /// </summary>
    public static IServiceCollection AddPagSeguroPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(PagSeguroOptions.ConfigSection);
        services.Configure<PagSeguroOptions>(section);

        var probe = section.Get<PagSeguroOptions>() ?? new PagSeguroOptions();
        if (string.IsNullOrWhiteSpace(probe.ApiToken))
            throw new ProviderConfigurationException("pagseguro", $"{PagSeguroOptions.ConfigSection}:ApiToken is required");

        services.AddHttpClient<PagSeguroPaymentProvider>();
        services.AddTransient<IPaymentGatewayProvider, PagSeguroPaymentProvider>(sp =>
            sp.GetRequiredService<PagSeguroPaymentProvider>());
        services.AddTransient<IPayoutProvider, PagSeguroPaymentProvider>(sp =>
            sp.GetRequiredService<PagSeguroPaymentProvider>());

        services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.PagSeguro, (sp, _) => sp.GetRequiredService<PagSeguroPaymentProvider>());

        // Recurring billing (PagBank /recurring/orders).
        services.AddHttpClient<PagSeguroSubscriptionProvider>();
        services.AddTransient<ISubscriptionProvider, PagSeguroSubscriptionProvider>(sp => sp.GetRequiredService<PagSeguroSubscriptionProvider>());
        services.AddKeyedTransient<ISubscriptionProvider>(ProviderNames.PagSeguro, (sp, _) => sp.GetRequiredService<PagSeguroSubscriptionProvider>());

        // PIX QR code generation.
        services.AddHttpClient<PagSeguroQrCodeProvider>();
        services.AddTransient<IQrCodeProvider, PagSeguroQrCodeProvider>(sp => sp.GetRequiredService<PagSeguroQrCodeProvider>());
        services.AddKeyedTransient<IQrCodeProvider>(ProviderNames.PagSeguro, (sp, _) => sp.GetRequiredService<PagSeguroQrCodeProvider>());

        // Server-side card tokenisation (SAQ-D).
        services.AddHttpClient<PagSeguroTokenisationProvider>();
        services.AddTransient<ITokenisationProvider, PagSeguroTokenisationProvider>(sp => sp.GetRequiredService<PagSeguroTokenisationProvider>());
        services.AddKeyedTransient<ITokenisationProvider>(ProviderNames.PagSeguro, (sp, _) => sp.GetRequiredService<PagSeguroTokenisationProvider>());

        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
