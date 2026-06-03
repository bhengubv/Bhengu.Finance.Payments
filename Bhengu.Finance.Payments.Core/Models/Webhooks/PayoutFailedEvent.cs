// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Webhooks;

/// <summary>
/// A payout to a destination failed and funds were returned to the merchant balance.
/// </summary>
public sealed record PayoutFailedEvent : WebhookEvent
{
    /// <summary>The payout's gateway reference.</summary>
    public required string PayoutReference { get; init; }

    /// <summary>Amount that failed to be paid out.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>Provider's failure code (e.g. "invalid_account_number", "beneficiary_bank_unreachable").</summary>
    public string? FailureCode { get; init; }

    /// <summary>Human-readable failure description.</summary>
    public string? FailureMessage { get; init; }
}
