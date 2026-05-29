// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models;

/// <summary>
/// A normalised webhook event after the provider's payload has been parsed and verified.
/// </summary>
public sealed record WebhookEvent
{
    /// <summary>The provider's transaction reference the event relates to (matches PaymentResponse.GatewayReference).</summary>
    public required string GatewayReference { get; init; }

    /// <summary>New lifecycle status implied by the event.</summary>
    public required PaymentStatus Status { get; init; }

    /// <summary>Provider-specific event type label (e.g. "payment.completed", "charge.refunded").</summary>
    public string? EventType { get; init; }

    /// <summary>The raw provider payload, parsed into key/value form, for callers that need fields the SDK didn't normalise.</summary>
    public IReadOnlyDictionary<string, string>? RawPayload { get; init; }
}
