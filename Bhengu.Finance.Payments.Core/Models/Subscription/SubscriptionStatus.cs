// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Subscription;

/// <summary>
/// Normalised subscription lifecycle status.
/// </summary>
public enum SubscriptionStatus
{
    /// <summary>Subscription is active and will renew on the next billing date.</summary>
    Active,

    /// <summary>Subscription is paused — no charges run until resumed.</summary>
    Paused,

    /// <summary>Subscription is in a trial window and has not yet been charged.</summary>
    Trialing,

    /// <summary>Most recent charge failed; provider is retrying per its dunning policy.</summary>
    PastDue,

    /// <summary>Subscription was cancelled — no further charges will run.</summary>
    Cancelled,

    /// <summary>Subscription has reached the end of its term and completed normally.</summary>
    Expired
}
