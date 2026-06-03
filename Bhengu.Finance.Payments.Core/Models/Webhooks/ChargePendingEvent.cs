// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Webhooks;

/// <summary>
/// A charge is in flight — the provider has accepted the instruction but settlement is still pending.
/// Common for redirect-flow and mobile-money providers where the payer hasn't completed approval yet.
/// </summary>
public sealed record ChargePendingEvent : WebhookEvent
{
    /// <summary>Amount the provider is attempting to charge.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }
}
