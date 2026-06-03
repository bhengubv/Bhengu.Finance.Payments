// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Mandate;

/// <summary>
/// An active or historical debit-order mandate.
/// </summary>
public sealed record Mandate
{
    /// <summary>The mandate's gateway reference.</summary>
    public required string Reference { get; init; }

    /// <summary>Vault customer the mandate belongs to.</summary>
    public required string CustomerId { get; init; }

    /// <summary>Current lifecycle status.</summary>
    public required MandateStatus Status { get; init; }

    /// <summary>Maximum amount the merchant may pull per debit.</summary>
    public required decimal AmountLimit { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>UTC date the mandate was authorised.</summary>
    public DateTime? AuthorisedAt { get; init; }

    /// <summary>UTC date the mandate was cancelled.</summary>
    public DateTime? CancelledAt { get; init; }

    /// <summary>
    /// Set when the provider requires the payer to authorise via redirect — e.g. Stitch DebiCheck,
    /// Stripe SEPA mandate setup. Consumers redirect the payer here; status updates arrive via webhook.
    /// </summary>
    public string? AuthorisationUrl { get; init; }
}
