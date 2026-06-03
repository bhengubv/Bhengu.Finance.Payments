// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Marketplace;

/// <summary>
/// A reusable split definition that routes a charge across multiple sub-accounts.
/// Most providers let you create the split once and reference it from many charges.
/// </summary>
public sealed record SplitDefinition
{
    /// <summary>The split's gateway reference. Pass on <see cref="ChargeWithSplitRequest.SplitReference"/>.</summary>
    public required string Reference { get; init; }

    /// <summary>Human-readable name.</summary>
    public required string Name { get; init; }

    /// <summary>ISO 4217 currency code the split is denominated in.</summary>
    public required string Currency { get; init; }

    /// <summary>The beneficiary rules. Order is significant for fixed-amount splits — remainder typically goes to the platform account.</summary>
    public required IReadOnlyList<SplitRule> Rules { get; init; }
}
