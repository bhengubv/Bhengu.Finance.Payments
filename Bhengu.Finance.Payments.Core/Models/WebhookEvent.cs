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

    /// <summary>
    /// Provider-specific event type label, echoed verbatim from the upstream payload
    /// (e.g. "payment_intent.succeeded" for Stripe, "charge.success" for Paystack, "payment.captured"
    /// for Razorpay). Kept for diagnostics and audit only. <b>Consumers should switch on
    /// <see cref="Category"/>, not on this string</b> — upstream providers occasionally rename
    /// event types or use different strings for the same logical event across API versions.
    /// </summary>
    public string? EventType { get; init; }

    /// <summary>
    /// Normalised event family — the stable consumer-facing classification. Every provider's
    /// <c>ParseWebhookAsync</c> MUST set this; falling back to <see cref="WebhookEventCategory.Unknown"/>
    /// is only acceptable for events the provider's adapter has not been taught to classify
    /// (typically because the upstream introduced a new event type after the SDK shipped).
    /// </summary>
    public required WebhookEventCategory Category { get; init; }

    /// <summary>The raw provider payload, parsed into key/value form, for callers that need fields the SDK didn't normalise.</summary>
    public IReadOnlyDictionary<string, string>? RawPayload { get; init; }
}
