// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bhengu.Finance.Payments.Core.Validation;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the <see cref="BhenguPaymentStartupValidator"/> hosted service exactly once per
    /// DI container. Called automatically by every <c>AddXxxPayments</c> extension so that if any
    /// provider is misconfigured the app crashes at startup instead of at first request.
    /// </summary>
    public static IServiceCollection AddBhenguPaymentStartupValidation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(d => d.ImplementationType == typeof(BhenguPaymentStartupValidator)))
            return services; // already registered
        services.AddSingleton<IHostedService, BhenguPaymentStartupValidator>();
        return services;
    }
}
