// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models;

/// <summary>
/// A request to pay funds out to a destination (bank account, mobile wallet, card).
/// </summary>
public sealed record PayoutRequest
{
    /// <summary>The provider-specific token representing the destination.</summary>
    public required string DestinationToken { get; init; }

    /// <summary>Amount in the major currency unit.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>Human-readable description.</summary>
    public required string Description { get; init; }
}
