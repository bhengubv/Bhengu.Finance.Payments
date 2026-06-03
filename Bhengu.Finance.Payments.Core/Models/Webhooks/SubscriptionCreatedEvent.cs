// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Webhooks;

/// <summary>
/// A subscription was created and is now active. Note: this event does NOT imply a charge has been
/// taken — a separate <see cref="SubscriptionRenewedEvent"/> fires when the first billing cycle settles.
/// </summary>
public sealed record SubscriptionCreatedEvent : WebhookEvent
{
    /// <summary>The subscription's gateway reference.</summary>
    public required string SubscriptionReference { get; init; }

    /// <summary>Provider plan identifier the subscription is bound to.</summary>
    public required string PlanReference { get; init; }

    /// <summary>Vault customer identifier.</summary>
    public string? CustomerId { get; init; }

    /// <summary>UTC timestamp the first billing cycle will run.</summary>
    public DateTime? NextBillingAt { get; init; }
}
