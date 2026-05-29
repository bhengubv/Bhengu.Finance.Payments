// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models;

/// <summary>
/// The provider's response after attempting a refund.
/// </summary>
public sealed record RefundResponse
{
    /// <summary>The refund's own gateway reference (distinct from the original payment's reference).</summary>
    public required string GatewayReference { get; init; }

    /// <summary>The amount actually refunded.</summary>
    public required decimal Amount { get; init; }

    /// <summary>Lifecycle status of the refund itself.</summary>
    public required PaymentStatus Status { get; init; }

    /// <summary>UTC timestamp the provider stamped the response.</summary>
    public DateTime ProcessedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Provider-supplied free-text message.</summary>
    public string? Message { get; init; }
}
