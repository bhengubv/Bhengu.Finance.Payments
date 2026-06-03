// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Mandate;

/// <summary>
/// Lifecycle status of a debit-order / pull-payment mandate.
/// </summary>
public enum MandateStatus
{
    /// <summary>Mandate is awaiting the payer's authorisation (and the bank's, for DebiCheck-style schemes).</summary>
    Pending,

    /// <summary>Mandate is active and may be charged via <see cref="Interfaces.IMandateProvider.ChargeMandateAsync"/>.</summary>
    Active,

    /// <summary>Mandate is paused — charges suspended until resumed.</summary>
    Paused,

    /// <summary>Mandate has been cancelled by the payer, bank, or merchant.</summary>
    Cancelled,

    /// <summary>Mandate has reached its end date.</summary>
    Expired,

    /// <summary>Mandate authorisation was rejected by the payer or bank.</summary>
    Rejected
}
