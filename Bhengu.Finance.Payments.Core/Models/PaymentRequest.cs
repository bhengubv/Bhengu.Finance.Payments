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

    /// <summary>
    /// Caller-supplied idempotency key. When set, providers that support <see cref="ProviderCapabilities.Idempotency"/>
    /// will deduplicate retries against the original response — so a network timeout followed by a client retry will
    /// NOT double-charge. Use a UUID per logical operation (e.g. one per order placement), not per HTTP request.
    /// Providers that do not support idempotency MUST ignore this field rather than fail.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// Optional vault-customer identifier. Required when charging a token belonging to a saved customer
    /// (Stripe Customer, Paystack Customer, Razorpay Contact, etc.). Null for one-off charges where the
    /// token itself fully identifies the payer.
    /// </summary>
    public string? CustomerId { get; init; }

    /// <summary>
    /// Optional 3DS / SCA completion payload from a prior challenge. Set when resuming a payment after the
    /// payer completed an authentication step-up flow. See <see cref="ThreeDSecure.ThreeDSecureCompletion"/>.
    /// </summary>
    public ThreeDSecure.ThreeDSecureCompletion? ThreeDSecureCompletion { get; init; }

    /// <summary>Provider-specific extension fields (e.g. m_payment_id for PayFast, idempotency_key for Stripe).</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
