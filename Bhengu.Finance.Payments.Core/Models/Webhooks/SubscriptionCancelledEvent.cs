// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Webhooks;

/// <summary>
/// A subscription was cancelled, paused, or expired — no further charges will be attempted.
/// </summary>
public sealed record SubscriptionCancelledEvent : WebhookEvent
{
    /// <summary>The subscription's gateway reference.</summary>
    public required string SubscriptionReference { get; init; }

    /// <summary>Who or what cancelled the subscription (e.g. "customer", "merchant", "payment_failed", "plan_completed").</summary>
    public string? CancellationReason { get; init; }
}
