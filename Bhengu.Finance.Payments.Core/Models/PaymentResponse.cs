// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models;

/// <summary>
/// The provider's response after attempting a payment.
/// </summary>
public sealed record PaymentResponse
{
    /// <summary>The provider's transaction identifier — required for refund and reconciliation.</summary>
    public required string GatewayReference { get; init; }

    /// <summary>Normalised lifecycle status.</summary>
    public required PaymentStatus Status { get; init; }

    /// <summary>The amount actually settled (may differ from the requested amount if the provider applied currency conversion).</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code of the settled amount.</summary>
    public required string Currency { get; init; }

    /// <summary>UTC timestamp the provider stamped the response.</summary>
    public DateTime ProcessedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Provider-supplied free-text message (e.g. "card approved", "insufficient funds").</summary>
    public string? Message { get; init; }
}
