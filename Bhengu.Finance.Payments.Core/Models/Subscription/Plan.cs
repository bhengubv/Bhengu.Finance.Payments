// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Subscription;

/// <summary>
/// A recurring-billing plan — the price + cadence template that subscriptions are created against.
/// </summary>
public sealed record Plan
{
    /// <summary>The plan's gateway reference. Pass as <c>SubscriptionRequest.PlanReference</c>.</summary>
    public required string Reference { get; init; }

    /// <summary>Human-readable plan name (e.g. "Pro Monthly", "Starter Annual").</summary>
    public required string Name { get; init; }

    /// <summary>Amount per billing cycle in the major currency unit.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>How often the plan charges.</summary>
    public required SubscriptionInterval Interval { get; init; }

    /// <summary>
    /// Number of intervals before the plan ends (e.g. 12 monthly = one year). Null for indefinite plans
    /// that run until the customer cancels.
    /// </summary>
    public int? TotalCycles { get; init; }

    /// <summary>Optional plan description.</summary>
    public string? Description { get; init; }
}
