// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Dispute;

/// <summary>
/// Normalised dispute (chargeback) lifecycle status.
/// </summary>
public enum DisputeStatus
{
    /// <summary>Dispute is open and awaiting evidence from the merchant.</summary>
    NeedsResponse,

    /// <summary>Evidence has been submitted; awaiting issuer decision.</summary>
    UnderReview,

    /// <summary>Dispute was resolved in the merchant's favour.</summary>
    Won,

    /// <summary>Dispute was resolved in the payer's favour — funds reversed.</summary>
    Lost,

    /// <summary>Merchant accepted the dispute without contesting.</summary>
    Accepted,

    /// <summary>The card scheme escalated the dispute to arbitration after a representment.</summary>
    Arbitration,

    /// <summary>Dispute expired without merchant response and defaulted to the payer.</summary>
    Expired
}
