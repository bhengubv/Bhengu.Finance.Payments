// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Subscription;

/// <summary>
/// An active or historical subscription instance bound to a plan and a customer.
/// </summary>
public sealed record Subscription
{
    /// <summary>The subscription's gateway reference.</summary>
    public required string Reference { get; init; }

    /// <summary>Plan the subscription is bound to.</summary>
    public required string PlanReference { get; init; }

    /// <summary>Vault customer identifier.</summary>
    public required string CustomerId { get; init; }

    /// <summary>Current lifecycle status.</summary>
    public required SubscriptionStatus Status { get; init; }

    /// <summary>UTC timestamp the subscription started.</summary>
    public required DateTime StartedAt { get; init; }

    /// <summary>UTC timestamp the next billing cycle will run. Null for cancelled / expired subscriptions.</summary>
    public DateTime? NextBillingAt { get; init; }

    /// <summary>UTC timestamp the subscription was cancelled, if applicable.</summary>
    public DateTime? CancelledAt { get; init; }

    /// <summary>Number of cycles billed to date.</summary>
    public int CyclesCompleted { get; init; }

    /// <summary>
    /// Set when the provider requires the payer to authorise via redirect before the subscription
    /// becomes active — e.g. PayFast's hosted-page recurring-token flow, MercadoPago Preapproval
    /// pending-approval URL. Consumers redirect the payer here; status updates arrive via webhook.
    /// Null for providers whose subscription creation completes server-side.
    /// </summary>
    public string? AuthorisationUrl { get; init; }
}
