// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Subscription;

/// <summary>
/// A request to create a recurring-billing plan.
/// </summary>
public sealed record PlanRequest
{
    /// <summary>Human-readable plan name.</summary>
    public required string Name { get; init; }

    /// <summary>Amount per billing cycle in the major currency unit.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>How often the plan charges.</summary>
    public required SubscriptionInterval Interval { get; init; }

    /// <summary>Number of intervals before the plan ends. Null for indefinite plans.</summary>
    public int? TotalCycles { get; init; }

    /// <summary>Optional plan description.</summary>
    public string? Description { get; init; }

    /// <summary>Caller-supplied idempotency key.</summary>
    public string? IdempotencyKey { get; init; }
}
