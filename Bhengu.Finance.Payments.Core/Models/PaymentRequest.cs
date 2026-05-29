// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models;

/// <summary>
/// A request to charge a previously-tokenised payment method.
/// </summary>
public sealed record PaymentRequest
{
    /// <summary>The provider-specific token representing the payment method (card, mandate, agreement).</summary>
    public required string PaymentMethodToken { get; init; }

    /// <summary>Amount in the major currency unit (e.g. 99.99 ZAR — not cents).</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>Human-readable description shown on statements where supported.</summary>
    public required string Description { get; init; }

    /// <summary>Provider-specific extension fields (e.g. m_payment_id for PayFast, idempotency_key for Stripe).</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
