// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Auditing;

/// <summary>
/// Process-wide audit sink read by <see cref="Providers.BhenguProviderBase"/>'s Run* wrappers.
/// Defaults to <see cref="NoopAuditLog"/> (writes nothing) so providers can be constructed
/// without a DI container in unit tests. <c>AddBhenguPaymentAuditLog</c> wires the configured
/// <see cref="IBhenguPaymentAuditLog"/> implementation into this slot at startup.
///
/// <para>Process-wide ambient state is unusual for the SDK, but appropriate here: audit is
/// write-only and fire-and-forget, every provider constructor would otherwise need to add an
/// <see cref="IBhenguPaymentAuditLog"/> parameter, and the failure mode (no audit emitted) is
/// benign rather than dangerous.</para>
/// </summary>
public static class BhenguPaymentAuditing
{
    private static IBhenguPaymentAuditLog s_default = NoopAuditLog.Instance;

    /// <summary>
    /// The active audit sink. Replace via <see cref="SetDefault"/> during DI bootstrap;
    /// <c>AddBhenguPaymentAuditLog</c> does this automatically.
    /// </summary>
    public static IBhenguPaymentAuditLog Default => s_default;

    /// <summary>
    /// Replace the active audit sink. Pass a non-null instance; pass
    /// <see cref="NoopAuditLog.Instance"/> to explicitly disable audit emission.
    /// </summary>
    public static void SetDefault(IBhenguPaymentAuditLog auditLog)
    {
        s_default = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
    }
}

/// <summary>
/// No-op <see cref="IBhenguPaymentAuditLog"/> — discards every entry. Used when no consumer-side
/// audit log has been registered, so the SDK never crashes for missing audit infrastructure.
/// </summary>
public sealed class NoopAuditLog : IBhenguPaymentAuditLog
{
    /// <summary>Singleton instance.</summary>
    public static readonly NoopAuditLog Instance = new();

    private NoopAuditLog() { }

    /// <inheritdoc />
    public Task RecordAsync(PaymentAuditEntry entry, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public async IAsyncEnumerable<PaymentAuditEntry> QueryAsync(
        PaymentAuditQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
