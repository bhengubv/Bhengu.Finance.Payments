// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bhengu.Finance.Payments.Core.Auditing;

/// <summary>
/// DI helpers for the audit-log subsystem.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the default <see cref="LoggerAuditLog"/>. Idempotent — production deployments
    /// should register a DB-backed <see cref="IBhenguPaymentAuditLog"/> BEFORE calling this so
    /// the <c>TryAdd</c> here becomes a no-op.
    /// </summary>
    public static IServiceCollection AddBhenguPaymentAuditLog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IBhenguPaymentAuditLog, LoggerAuditLog>();
        // Replace the process-wide ambient audit sink so BhenguProviderBase wrappers emit entries
        // through the consumer-registered implementation. Last-call-wins is acceptable — typical
        // hosts call this once at startup. Test code can call BhenguPaymentAuditing.SetDefault
        // directly to swap for an in-memory capture.
        services.AddHostedService<BhenguAuditingActivator>();
        return services;
    }

    /// <summary>
    /// Hosted service whose sole job is to copy the DI-resolved <see cref="IBhenguPaymentAuditLog"/>
    /// into the process-wide <see cref="BhenguPaymentAuditing.Default"/> slot at startup.
    /// </summary>
    private sealed class BhenguAuditingActivator(IBhenguPaymentAuditLog auditLog)
        : Microsoft.Extensions.Hosting.IHostedService
    {
        public Task StartAsync(CancellationToken ct)
        {
            BhenguPaymentAuditing.SetDefault(auditLog);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
