// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Dispute;

/// <summary>
/// A payer-initiated dispute (chargeback) against a settled charge.
/// </summary>
public sealed record Dispute
{
    /// <summary>The dispute's gateway reference.</summary>
    public required string Reference { get; init; }

    /// <summary>The original charge's gateway reference.</summary>
    public required string ChargeReference { get; init; }

    /// <summary>Disputed amount.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>Current lifecycle status.</summary>
    public required DisputeStatus Status { get; init; }

    /// <summary>Provider-supplied reason code (e.g. "fraudulent", "product_not_received", "duplicate").</summary>
    public string? ReasonCode { get; init; }

    /// <summary>Human-readable reason description.</summary>
    public string? ReasonDescription { get; init; }

    /// <summary>UTC timestamp the dispute was opened.</summary>
    public required DateTime OpenedAt { get; init; }

    /// <summary>UTC deadline for submitting evidence. After this the dispute auto-resolves to the payer.</summary>
    public DateTime? EvidenceDueBy { get; init; }

    /// <summary>Provider-charged chargeback fee, if known.</summary>
    public decimal? ChargebackFee { get; init; }
}
