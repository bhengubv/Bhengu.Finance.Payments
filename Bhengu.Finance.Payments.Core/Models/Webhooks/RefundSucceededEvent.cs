// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Webhooks;

/// <summary>
/// A refund completed — funds returned to the payer.
/// </summary>
public sealed record RefundSucceededEvent : WebhookEvent
{
    /// <summary>The refund's own gateway reference (distinct from the original charge's GatewayReference, which is on the base record).</summary>
    public required string RefundReference { get; init; }

    /// <summary>Amount actually refunded.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>True when this is a partial refund relative to the original charge.</summary>
    public bool IsPartial { get; init; }
}
