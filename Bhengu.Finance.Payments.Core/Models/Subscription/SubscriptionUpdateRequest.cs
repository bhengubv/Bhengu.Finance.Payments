// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Subscription;

/// <summary>
/// The fields that can be changed on an existing subscription via
/// <see cref="Interfaces.ISubscriptionUpdateSupport.UpdateSubscriptionAsync"/>. Every property is
/// optional — only the ones you set are sent to the provider; the rest are left unchanged.
/// </summary>
public sealed record SubscriptionUpdateRequest
{
    /// <summary>New recurring amount in the major currency unit (e.g. 99.99 — not cents). Null leaves it unchanged.</summary>
    public decimal? Amount { get; init; }

    /// <summary>New billing interval. Null leaves it unchanged.</summary>
    public SubscriptionInterval? Interval { get; init; }

    /// <summary>New next-billing date (the date part is used). Null leaves it unchanged.</summary>
    public DateTime? NextBillingDate { get; init; }

    /// <summary>New number of remaining billing cycles (0 = until cancelled, where the provider supports it). Null leaves it unchanged.</summary>
    public int? RemainingCycles { get; init; }
}
