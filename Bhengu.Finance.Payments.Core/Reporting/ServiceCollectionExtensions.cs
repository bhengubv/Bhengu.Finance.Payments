// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bhengu.Finance.Payments.Core.Reporting;

/// <summary>
/// DI registration helpers for the cross-provider reporting aggregator.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="IPaymentReportingAggregator"/>. The aggregator picks up every
    /// <see cref="Interfaces.ISettlementProvider"/> registered in the container, so registration
    /// order with the provider <c>Add*Payments</c> calls doesn't matter.
    /// </summary>
    public static IServiceCollection AddBhenguPaymentReporting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IPaymentReportingAggregator, PaymentReportingAggregator>();
        return services;
    }
}
