// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Webhooks;

/// <summary>
/// A debit-order / pull-payment mandate was authorised by the payer (and where applicable by their bank).
/// Future <see cref="Interfaces.IMandateProvider.ChargeMandateAsync"/> calls will succeed.
/// </summary>
public sealed record MandateActivatedEvent : WebhookEvent
{
    /// <summary>The mandate's gateway reference.</summary>
    public required string MandateReference { get; init; }

    /// <summary>Maximum amount that can be pulled per debit, if the scheme imposes a cap.</summary>
    public decimal? AmountLimit { get; init; }

    /// <summary>ISO 4217 currency code of the AmountLimit.</summary>
    public string? Currency { get; init; }
}
