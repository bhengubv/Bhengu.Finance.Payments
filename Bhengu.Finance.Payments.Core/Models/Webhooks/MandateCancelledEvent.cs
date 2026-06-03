// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Webhooks;

/// <summary>
/// A debit-order mandate was cancelled by the payer or their bank — future debit attempts will fail.
/// </summary>
public sealed record MandateCancelledEvent : WebhookEvent
{
    /// <summary>The mandate's gateway reference.</summary>
    public required string MandateReference { get; init; }

    /// <summary>Who or what cancelled it (e.g. "payer", "bank", "merchant", "expired").</summary>
    public string? CancellationReason { get; init; }
}
