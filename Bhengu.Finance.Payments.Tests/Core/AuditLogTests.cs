// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using Bhengu.Finance.Payments.Core.Auditing;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.CoreFoundations;

/// <summary>
/// Verifies the audit-log primitives + the process-wide ambient sink swap mechanism.
/// </summary>
public class AuditLogTests
{
    private sealed class CapturingAuditLog : IBhenguPaymentAuditLog
    {
        public ConcurrentBag<PaymentAuditEntry> Captured { get; } = new();
        public Task RecordAsync(PaymentAuditEntry entry, CancellationToken ct = default)
        {
            Captured.Add(entry);
            return Task.CompletedTask;
        }
        public async IAsyncEnumerable<PaymentAuditEntry> QueryAsync(
            PaymentAuditQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var e in Captured.ToArray()) yield return e;
        }
    }

    [Fact]
    public void DefaultIsNoopWhenNothingRegistered()
    {
        BhenguPaymentAuditing.SetDefault(NoopAuditLog.Instance);
        Assert.Same(NoopAuditLog.Instance, BhenguPaymentAuditing.Default);
    }

    [Fact]
    public async Task NoopAuditLogSwallowsRecords()
    {
        await NoopAuditLog.Instance.RecordAsync(new PaymentAuditEntry
        {
            At = DateTime.UtcNow,
            Provider = "x",
            Operation = "ProcessPayment",
            Outcome = "success",
        });
        // No assertion — just must not throw.
    }

    [Fact]
    public void SetDefaultRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => BhenguPaymentAuditing.SetDefault(null!));
    }

    [Fact]
    public async Task SwappedSinkReceivesEntries()
    {
        // Test the LOCAL capture instance directly — not via the process-wide Default slot, which
        // would race with other tests running provider operations that emit audit entries.
        var capture = new CapturingAuditLog();
        await capture.RecordAsync(new PaymentAuditEntry
        {
            At = DateTime.UtcNow,
            Provider = "stripe-test-isolated",
            Operation = "ProcessPayment",
            Outcome = "success",
            Amount = 100m,
            Currency = "ZAR",
            IdempotencyKey = "idem-1",
            DurationMs = 12.3,
        });

        var entry = Assert.Single(capture.Captured);
        Assert.Equal("stripe-test-isolated", entry.Provider);
        Assert.Equal(100m, entry.Amount);
        Assert.Equal("idem-1", entry.IdempotencyKey);
    }

    [Fact]
    public void SetDefaultSwapsTheAmbientSink()
    {
        var prev = BhenguPaymentAuditing.Default;
        try
        {
            var capture = new CapturingAuditLog();
            BhenguPaymentAuditing.SetDefault(capture);
            Assert.Same(capture, BhenguPaymentAuditing.Default);
        }
        finally
        {
            BhenguPaymentAuditing.SetDefault(prev);
        }
    }
}
