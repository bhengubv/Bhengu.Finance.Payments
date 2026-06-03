// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Settlement;

/// <summary>
/// A settlement batch — the funds payout the provider made to the merchant's bank account on a given day.
/// Use the constituent <see cref="SettlementTransaction"/> list for line-by-line reconciliation against
/// your own ledger.
/// </summary>
public sealed record Settlement
{
    /// <summary>The settlement's gateway reference.</summary>
    public required string Reference { get; init; }

    /// <summary>Total amount credited to the merchant after provider fees.</summary>
    public required decimal NetAmount { get; init; }

    /// <summary>Gross amount before provider fees.</summary>
    public decimal? GrossAmount { get; init; }

    /// <summary>Total provider fees deducted.</summary>
    public decimal? Fees { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>UTC timestamp the provider initiated the settlement.</summary>
    public required DateTime SettledAt { get; init; }

    /// <summary>The merchant bank account reference the funds were paid to.</summary>
    public string? BankAccountReference { get; init; }

    /// <summary>Number of constituent transactions.</summary>
    public int TransactionCount { get; init; }
}
