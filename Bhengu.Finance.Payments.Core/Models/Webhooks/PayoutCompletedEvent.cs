// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Webhooks;

/// <summary>
/// A payout to a destination (bank account, mobile wallet) completed — funds have left the provider.
/// </summary>
public sealed record PayoutCompletedEvent : WebhookEvent
{
    /// <summary>The payout's gateway reference.</summary>
    public required string PayoutReference { get; init; }

    /// <summary>Amount paid out.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>Destination token the funds were sent to.</summary>
    public string? DestinationToken { get; init; }
}
