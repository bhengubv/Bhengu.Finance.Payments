// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Subscription;

/// <summary>
/// A request to subscribe a customer to a plan.
/// </summary>
public sealed record SubscriptionRequest
{
    /// <summary>The plan to subscribe to.</summary>
    public required string PlanReference { get; init; }

    /// <summary>The vault customer being subscribed.</summary>
    public required string CustomerId { get; init; }

    /// <summary>The vaulted payment-method token that will be charged each cycle.</summary>
    public required string PaymentMethodToken { get; init; }

    /// <summary>UTC start date. Null means start immediately.</summary>
    public DateTime? StartAt { get; init; }

    /// <summary>Number of trial days before the first charge. Null or 0 means no trial.</summary>
    public int? TrialDays { get; init; }

    /// <summary>Caller-supplied idempotency key.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Provider-specific extension fields.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
