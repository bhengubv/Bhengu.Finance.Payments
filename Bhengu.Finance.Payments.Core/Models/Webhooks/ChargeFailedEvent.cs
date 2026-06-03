// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Webhooks;

/// <summary>
/// A charge attempt was declined or otherwise failed terminally.
/// </summary>
public sealed record ChargeFailedEvent : WebhookEvent
{
    /// <summary>Amount the provider attempted to charge.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>Provider's error code (e.g. "card_declined", "insufficient_funds"). Null where not supplied.</summary>
    public string? FailureCode { get; init; }

    /// <summary>Human-readable failure description.</summary>
    public string? FailureMessage { get; init; }
}
