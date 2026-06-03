// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Webhooks;

/// <summary>
/// A refund attempt failed — the original charge remains settled.
/// </summary>
public sealed record RefundFailedEvent : WebhookEvent
{
    /// <summary>Amount the provider attempted to refund.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>Provider's error code (e.g. "refund_window_expired", "insufficient_settlement_balance").</summary>
    public string? FailureCode { get; init; }

    /// <summary>Human-readable failure description.</summary>
    public string? FailureMessage { get; init; }
}
