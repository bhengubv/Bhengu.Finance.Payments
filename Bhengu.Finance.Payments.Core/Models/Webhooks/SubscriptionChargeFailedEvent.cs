// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Webhooks;

/// <summary>
/// A subscription's recurring charge failed (insufficient funds, expired card, etc.). The
/// subscription may still be active depending on the provider's retry policy.
/// </summary>
public sealed record SubscriptionChargeFailedEvent : WebhookEvent
{
    /// <summary>The subscription's gateway reference.</summary>
    public required string SubscriptionReference { get; init; }

    /// <summary>Amount the provider attempted to charge.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>Provider's failure code.</summary>
    public string? FailureCode { get; init; }

    /// <summary>UTC timestamp the provider will retry the charge, if any.</summary>
    public DateTime? NextRetryAt { get; init; }
}
