// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Models.Webhooks;

namespace Bhengu.Finance.Payments.Core.Models;

/// <summary>
/// A normalised webhook event after the provider's payload has been parsed and verified.
/// Providers that support <see cref="ProviderCapabilities.TypedWebhooks"/> return a derived
/// record (e.g. <c>ChargeSucceededEvent</c>, <c>DisputeOpenedEvent</c>) so consumers can
/// <c>switch (event)</c> on the concrete type instead of string-matching on <see cref="EventType"/>.
/// </summary>
public record WebhookEvent
{
    /// <summary>The provider's transaction reference the event relates to (matches PaymentResponse.GatewayReference).</summary>
    public required string GatewayReference { get; init; }

    /// <summary>New lifecycle status implied by the event.</summary>
    public required PaymentStatus Status { get; init; }

    /// <summary>Provider-specific event type label (e.g. "payment.completed", "charge.refunded").</summary>
    public string? EventType { get; init; }

    /// <summary>
    /// Normalised event family. <see cref="WebhookEventCategory.Unknown"/> for legacy events that
    /// haven't been classified into a typed sub-record yet. Lets consumers route on a stable enum
    /// even when the concrete event type isn't recognised.
    /// </summary>
    public WebhookEventCategory Category { get; init; } = WebhookEventCategory.Unknown;

    /// <summary>The raw provider payload, parsed into key/value form, for callers that need fields the SDK didn't normalise.</summary>
    public IReadOnlyDictionary<string, string>? RawPayload { get; init; }
}
