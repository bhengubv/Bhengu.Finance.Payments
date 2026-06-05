// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Auditing;

/// <summary>
/// Tamper-evident audit log of every charge / refund / payout / dispute action the SDK performs
/// on the caller's behalf. Mandatory for SARB / FICA / POPIA compliance and PCI-DSS requirement 10.
///
/// <para>Default implementation is <see cref="LoggerAuditLog"/> — writes structured entries to
/// <c>ILogger</c> at <c>Information</c> level. Production deployments should swap for an
/// append-only DB-backed store (Postgres with row-level immutability, AWS QLDB, etc.) by
/// registering their own implementation AFTER calling <c>AddBhenguPaymentAuditLog</c>.</para>
///
/// <para>Audit entries are emitted automatically by <see cref="Providers.BhenguProviderBase"/>'s
/// Run* wrappers — consumers do not have to manually log. The interface is exposed so consumers
/// can record bespoke business-event audit entries alongside SDK ones.</para>
/// </summary>
public interface IBhenguPaymentAuditLog
{
    /// <summary>
    /// Record an audit entry. MUST NOT throw — the caller is in the middle of a financial
    /// operation and audit-log failure should never bring it down. Implementations should
    /// catch + log internally and fail open.
    /// </summary>
    Task RecordAsync(PaymentAuditEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Query historical entries — for compliance reporting, dispute investigation, reconciliation.
    /// Implementations are expected to support tenant-scoped queries when running multi-tenant.
    /// </summary>
    IAsyncEnumerable<PaymentAuditEntry> QueryAsync(PaymentAuditQuery query, CancellationToken ct = default);
}

/// <summary>An immutable audit entry.</summary>
public sealed record PaymentAuditEntry
{
    /// <summary>UTC timestamp of the action.</summary>
    public required DateTime At { get; init; }

    /// <summary>Provider name (e.g. "stripe", "payfast").</summary>
    public required string Provider { get; init; }

    /// <summary>SDK operation name (e.g. "ProcessPayment", "ProcessRefund", "GenerateQr").</summary>
    public required string Operation { get; init; }

    /// <summary>Outcome — success / declined / rate_limited / unavailable / error.</summary>
    public required string Outcome { get; init; }

    /// <summary>Tenant id, when running multi-tenant. Null for single-tenant deployments.</summary>
    public string? TenantId { get; init; }

    /// <summary>The provider's transaction reference, when the operation produced one.</summary>
    public string? GatewayReference { get; init; }

    /// <summary>Amount in the major currency unit. Null for non-financial operations (vault, query).</summary>
    public decimal? Amount { get; init; }

    /// <summary>ISO 4217 currency. Null for non-financial operations.</summary>
    public string? Currency { get; init; }

    /// <summary>Caller-supplied idempotency key, when one was passed.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Operation duration in milliseconds.</summary>
    public double DurationMs { get; init; }

    /// <summary>
    /// When <see cref="Outcome"/> is not "success", the canonical SDK exception type that was
    /// thrown (e.g. "PaymentDeclinedException", "ProviderUnavailableException").
    /// </summary>
    public string? ErrorType { get; init; }

    /// <summary>Provider-supplied error code, when present.</summary>
    public string? ErrorCode { get; init; }
}

/// <summary>Query filter for <see cref="IBhenguPaymentAuditLog.QueryAsync"/>.</summary>
public sealed record PaymentAuditQuery
{
    /// <summary>Inclusive lower bound on <see cref="PaymentAuditEntry.At"/>.</summary>
    public DateTime? FromUtc { get; init; }
    /// <summary>Inclusive upper bound on <see cref="PaymentAuditEntry.At"/>.</summary>
    public DateTime? ToUtc { get; init; }
    /// <summary>Restrict to a single provider.</summary>
    public string? Provider { get; init; }
    /// <summary>Restrict to a single tenant.</summary>
    public string? TenantId { get; init; }
    /// <summary>Restrict to a single outcome.</summary>
    public string? Outcome { get; init; }
    /// <summary>Maximum entries to return.</summary>
    public int MaxItems { get; init; } = 1000;
}
