// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Webhooks;

/// <summary>
/// A dispute was resolved in the merchant's favour — the original charge remains settled and any
/// withheld funds are released.
/// </summary>
public sealed record DisputeWonEvent : WebhookEvent
{
    /// <summary>The dispute's gateway reference.</summary>
    public required string DisputeReference { get; init; }

    /// <summary>Amount that remains with the merchant.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }
}
