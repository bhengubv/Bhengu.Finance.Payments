// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models;

/// <summary>
/// The provider's response after attempting a payout.
/// </summary>
public sealed record PayoutResponse
{
    /// <summary>The payout's gateway reference.</summary>
    public required string GatewayReference { get; init; }

    /// <summary>Lifecycle status of the payout.</summary>
    public required PaymentStatus Status { get; init; }

    /// <summary>Amount actually paid out.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>UTC timestamp the provider stamped the response.</summary>
    public DateTime ProcessedAt { get; init; } = DateTime.UtcNow;
}
