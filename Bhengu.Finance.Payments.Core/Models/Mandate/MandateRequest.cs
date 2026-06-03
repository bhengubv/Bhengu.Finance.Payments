// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Mandate;

/// <summary>
/// A request to set up a debit-order / pull-payment mandate against a payer's bank account.
/// </summary>
public sealed record MandateRequest
{
    /// <summary>The vault customer this mandate belongs to.</summary>
    public required string CustomerId { get; init; }

    /// <summary>
    /// Payer's bank-account token (provider-specific). For schemes that authorise via redirect
    /// instead of an upfront account number (Stitch DebiCheck, Stripe SEPA), pass an empty string
    /// and the provider returns a redirect URL.
    /// </summary>
    public required string BankAccountToken { get; init; }

    /// <summary>Maximum amount the merchant may pull per debit, in the major currency unit.</summary>
    public required decimal AmountLimit { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>Human-readable mandate description shown to the payer for authorisation.</summary>
    public required string Description { get; init; }

    /// <summary>UTC start date — the earliest debit may occur. Null means immediately.</summary>
    public DateTime? StartAt { get; init; }

    /// <summary>UTC end date. Null means open-ended (until cancelled).</summary>
    public DateTime? EndAt { get; init; }

    /// <summary>Caller-supplied idempotency key.</summary>
    public string? IdempotencyKey { get; init; }
}
