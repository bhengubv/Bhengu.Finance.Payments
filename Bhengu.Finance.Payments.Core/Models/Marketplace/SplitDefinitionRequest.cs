// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Marketplace;

/// <summary>
/// A request to create a reusable split definition.
/// </summary>
public sealed record SplitDefinitionRequest
{
    /// <summary>Human-readable name.</summary>
    public required string Name { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>The beneficiary rules.</summary>
    public required IReadOnlyList<SplitRule> Rules { get; init; }

    /// <summary>Caller-supplied idempotency key.</summary>
    public string? IdempotencyKey { get; init; }
}
