// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Marketplace;

/// <summary>
/// A single beneficiary line inside a <see cref="SplitDefinition"/>.
/// </summary>
public sealed record SplitRule
{
    /// <summary>The sub-account that receives this share.</summary>
    public required string SubAccountReference { get; init; }

    /// <summary>
    /// How the share is calculated. Either an absolute amount OR a percentage of the gross —
    /// both must not be set simultaneously.
    /// </summary>
    public required SplitShareType ShareType { get; init; }

    /// <summary>When <see cref="ShareType"/> is <see cref="SplitShareType.FixedAmount"/>, the amount.</summary>
    public decimal? Amount { get; init; }

    /// <summary>When <see cref="ShareType"/> is <see cref="SplitShareType.Percentage"/>, the percent 0-100.</summary>
    public decimal? Percentage { get; init; }

    /// <summary>True when the sub-account also bears its proportional share of the provider's fee.</summary>
    public bool BearsTransactionFee { get; init; }
}

/// <summary>How a beneficiary's share is calculated.</summary>
public enum SplitShareType
{
    /// <summary>A fixed amount in the gross currency.</summary>
    FixedAmount,
    /// <summary>A percentage 0-100 of the gross.</summary>
    Percentage
}
