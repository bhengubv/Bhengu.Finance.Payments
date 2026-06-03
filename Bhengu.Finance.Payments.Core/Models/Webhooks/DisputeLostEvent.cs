// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Webhooks;

/// <summary>
/// A dispute was resolved in the payer's favour — funds have been reversed back to the payer
/// and chargeback fees may apply.
/// </summary>
public sealed record DisputeLostEvent : WebhookEvent
{
    /// <summary>The dispute's gateway reference.</summary>
    public required string DisputeReference { get; init; }

    /// <summary>Amount reversed to the payer.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>Provider-charged chargeback fee, if any.</summary>
    public decimal? ChargebackFee { get; init; }
}
