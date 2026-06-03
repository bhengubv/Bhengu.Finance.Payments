// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Webhooks;

/// <summary>
/// A subscription's recurring charge succeeded — funds are committed for the new billing period.
/// </summary>
public sealed record SubscriptionRenewedEvent : WebhookEvent
{
    /// <summary>The subscription's gateway reference.</summary>
    public required string SubscriptionReference { get; init; }

    /// <summary>Amount charged for this billing cycle.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>UTC timestamp the next billing cycle will run.</summary>
    public DateTime? NextBillingAt { get; init; }
}
