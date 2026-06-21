// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.BricsPay.Configuration;
using Bhengu.Finance.Payments.BricsPay.Providers;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.BricsPay.Extensions;

/// <summary>DI registration for the BRICS Pay QR (Internet Acquiring) provider.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the BRICS Pay QR acquiring provider as an <see cref="IQrCodeProvider"/>. Reads configuration
    /// from <c>Bhengu:Finance:Payments:BricsPay</c>. See <c>BRICS_PAY_API_REFERENCE.md</c>.
    /// </summary>
    public static IServiceCollection AddBricsPayPayments(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(BricsPayOptions.ConfigSection);
        services.Configure<BricsPayOptions>(section);

        var probe = section.Get<BricsPayOptions>() ?? new BricsPayOptions();
        if (string.IsNullOrWhiteSpace(probe.TerminalId))
            throw new ProviderConfigurationException("bricspay", $"{BricsPayOptions.ConfigSection}:TerminalId is required");
        if (string.IsNullOrWhiteSpace(probe.BaseUrl))
            throw new ProviderConfigurationException("bricspay", $"{BricsPayOptions.ConfigSection}:BaseUrl is required");
        if (string.IsNullOrWhiteSpace(probe.PrivateKeyPem))
            throw new ProviderConfigurationException("bricspay", $"{BricsPayOptions.ConfigSection}:PrivateKeyPem is required");

        services.AddHttpClient<BricsPayPaymentProvider>();

        services.AddTransient<IQrCodeProvider, BricsPayPaymentProvider>(sp =>
            sp.GetRequiredService<BricsPayPaymentProvider>());

        // Register as keyed service so consumers can resolve by name: [FromKeyedServices(ProviderNames.BricsPay)]
        services.AddKeyedTransient<IQrCodeProvider>(ProviderNames.BricsPay,
            (sp, _) => sp.GetRequiredService<BricsPayPaymentProvider>());

        // Eager startup validation — fails the app at startup if config is broken (vs first request).
        services.AddBhenguPaymentStartupValidation();

        return services;
    }
}
