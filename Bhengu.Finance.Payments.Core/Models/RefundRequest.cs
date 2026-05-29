// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models;

/// <summary>
/// A request to refund a previously-completed payment.
/// </summary>
public sealed record RefundRequest
{
    /// <summary>The original payment's GatewayReference, as returned in PaymentResponse.</summary>
    public required string GatewayReference { get; init; }

    /// <summary>Amount to refund in the major currency unit. May be partial.</summary>
    public required decimal Amount { get; init; }

    /// <summary>Reason for the refund — required by some providers for audit.</summary>
    public required string Reason { get; init; }
}
