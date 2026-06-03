// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Mandate;

/// <summary>
/// A request to pull funds against an active mandate.
/// </summary>
public sealed record MandateChargeRequest
{
    /// <summary>The mandate to debit.</summary>
    public required string MandateReference { get; init; }

    /// <summary>Amount to debit in the major currency unit. MUST be ≤ the mandate's amount limit.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code. Should match the mandate currency.</summary>
    public required string Currency { get; init; }

    /// <summary>Human-readable description shown on the payer's bank statement.</summary>
    public required string Description { get; init; }

    /// <summary>Caller-supplied idempotency key.</summary>
    public string? IdempotencyKey { get; init; }
}
