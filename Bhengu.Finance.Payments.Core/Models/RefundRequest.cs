// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models;

/// <summary>
/// A request to refund a previously-completed payment.
/// </summary>
public sealed record RefundRequest
{
    /// <summary>The original payment's GatewayReference, as returned in PaymentResponse.</summary>
    public required string GatewayReference { get; init; }

    /// <summary>Amount to refund in the major currency unit. May be partial.</summary>
    public required decimal Amount { get; init; }

    /// <summary>Reason for the refund — required by some providers for audit.</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// The original payment amount, when known. Lets providers and consumers validate that a partial
    /// refund really IS partial (Amount &lt; OriginalAmount) — guards against accidental full refunds
    /// that callers thought were partial. Optional: leave null if the caller can't supply it.
    /// </summary>
    public decimal? OriginalAmount { get; init; }

    /// <summary>
    /// Caller-supplied idempotency key. Providers that support <see cref="ProviderCapabilities.Idempotency"/>
    /// deduplicate retries against the original response so a network blip won't double-refund. Use a UUID
    /// per logical refund decision, not per HTTP request.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// True when this is a partial refund and the provider must accept <c>Amount</c> less than the original
    /// charge. False (or null) for full refunds. Only meaningful when <see cref="OriginalAmount"/> is also set;
    /// otherwise providers fall back to their default partial-refund behaviour.
    /// </summary>
    public bool IsPartial => OriginalAmount.HasValue && Amount < OriginalAmount.Value;
}
