// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Settlement;

/// <summary>
/// A single line item inside a <see cref="Settlement"/> batch — typically a charge, refund, or chargeback.
/// </summary>
public sealed record SettlementTransaction
{
    /// <summary>The gateway reference of the source transaction (charge / refund / dispute).</summary>
    public required string GatewayReference { get; init; }

    /// <summary>What kind of transaction this is.</summary>
    public required SettlementTransactionKind Kind { get; init; }

    /// <summary>Net amount the merchant received (positive) or paid out (negative for refund / chargeback).</summary>
    public required decimal NetAmount { get; init; }

    /// <summary>Gross amount before fees.</summary>
    public decimal? GrossAmount { get; init; }

    /// <summary>Provider fee for this line item.</summary>
    public decimal? Fee { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>UTC timestamp the transaction was created (NOT when it settled).</summary>
    public required DateTime CreatedAt { get; init; }
}

/// <summary>What kind of line item a <see cref="SettlementTransaction"/> represents.</summary>
public enum SettlementTransactionKind
{
    /// <summary>A successful charge contributing positively to the batch.</summary>
    Charge,

    /// <summary>A refund debiting the merchant.</summary>
    Refund,

    /// <summary>A chargeback debiting the merchant.</summary>
    Chargeback,

    /// <summary>A provider fee charged separately from the transaction net (e.g. monthly statement fee).</summary>
    Fee,

    /// <summary>An adjustment for a previously-settled item (correction / reversal).</summary>
    Adjustment,

    /// <summary>Anything else the provider categorises.</summary>
    Other
}
