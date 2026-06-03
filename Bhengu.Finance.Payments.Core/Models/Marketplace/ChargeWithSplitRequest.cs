// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Models;

namespace Bhengu.Finance.Payments.Core.Models.Marketplace;

/// <summary>
/// A charge that automatically splits the proceeds across pre-defined beneficiaries.
/// </summary>
public sealed record ChargeWithSplitRequest
{
    /// <summary>The underlying charge instruction.</summary>
    public required PaymentRequest Payment { get; init; }

    /// <summary>
    /// Either a reference to a previously-created <see cref="SplitDefinition"/> (preferred when the
    /// split is reusable) OR null if <see cref="InlineRules"/> is provided instead.
    /// </summary>
    public string? SplitReference { get; init; }

    /// <summary>
    /// Inline beneficiary rules — use when the split is one-off and not worth persisting as a
    /// <see cref="SplitDefinition"/>. Must be null if <see cref="SplitReference"/> is set.
    /// </summary>
    public IReadOnlyList<SplitRule>? InlineRules { get; init; }
}
