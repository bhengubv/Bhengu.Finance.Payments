// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.Core.Auditing;

/// <summary>
/// Default <see cref="IBhenguPaymentAuditLog"/> implementation — writes structured entries to
/// <see cref="ILogger"/> at <see cref="LogLevel.Information"/>. Suitable for development and
/// single-instance deployments where logs flow into a queryable backend (Seq / Loki / ElasticSearch).
///
/// <para><b>Not suitable for production compliance use</b> on its own — there's no append-only
/// guarantee, no tamper detection, no efficient query. Swap for a DB-backed implementation that
/// uses append-only storage + checksum chains.</para>
/// </summary>
public sealed class LoggerAuditLog : IBhenguPaymentAuditLog
{
    private readonly ILogger<LoggerAuditLog> _logger;

    /// <summary>Construct the audit log with the provided logger.</summary>
    public LoggerAuditLog(ILogger<LoggerAuditLog> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task RecordAsync(PaymentAuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // Single structured log line per audit entry. Templated property names match the
        // structured-logging convention so consumers can route to a dedicated audit sink.
        _logger.LogInformation(
            "BhenguAudit at={At:O} provider={Provider} operation={Operation} outcome={Outcome} tenant={TenantId} ref={GatewayReference} amount={Amount} currency={Currency} idempotencyKey={IdempotencyKey} durationMs={DurationMs} errorType={ErrorType} errorCode={ErrorCode}",
            entry.At, entry.Provider, entry.Operation, entry.Outcome, entry.TenantId,
            entry.GatewayReference, entry.Amount, entry.Currency, entry.IdempotencyKey,
            entry.DurationMs, entry.ErrorType, entry.ErrorCode);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PaymentAuditEntry> QueryAsync(
        PaymentAuditQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // The logger-backed store has no historical query — entries flow to whatever sink the
        // host configures, which is where queries belong. Yield nothing.
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
