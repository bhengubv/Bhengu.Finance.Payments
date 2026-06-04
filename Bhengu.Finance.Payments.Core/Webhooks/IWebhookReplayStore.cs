// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Webhooks;

/// <summary>
/// Persistence contract for inbound webhook receipts. Consumers persist every verified webhook
/// payload through this store BEFORE acting on it, then use <see cref="IWebhookReplayer"/> to
/// re-deliver any event whose downstream handler failed.
///
/// <para>This is what makes "exactly-once" semantics achievable end-to-end — provider redelivery
/// only guarantees "at least once". By recording every receipt and dedupting on the provider's
/// event id, the SDK turns at-least-once into effectively-once for the consumer's handler.</para>
/// </summary>
public interface IWebhookReplayStore
{
    /// <summary>
    /// Persist a verified webhook envelope. Idempotent: persisting the same EventId twice is a no-op.
    /// </summary>
    Task<bool> TryRecordAsync(WebhookEnvelope envelope, CancellationToken ct = default);

    /// <summary>List envelopes whose downstream-handler outcome is still pending or failed.</summary>
    Task<IReadOnlyList<WebhookEnvelope>> ListPendingAsync(string providerName, int maxItems = 100, CancellationToken ct = default);

    /// <summary>Mark a recorded envelope as successfully handled.</summary>
    Task MarkHandledAsync(string eventId, CancellationToken ct = default);

    /// <summary>Mark a recorded envelope as failed with the failure reason captured.</summary>
    Task MarkFailedAsync(string eventId, string failureReason, CancellationToken ct = default);
}

/// <summary>
/// A verified webhook envelope captured by the SDK.
/// </summary>
public sealed record WebhookEnvelope
{
    /// <summary>Provider name (use <see cref="ProviderNames"/> constants).</summary>
    public required string ProviderName { get; init; }

    /// <summary>Provider-supplied event id used for dedup (e.g. Stripe evt_..., Paystack id, MercadoPago id).</summary>
    public required string EventId { get; init; }

    /// <summary>Raw payload string as received (used for re-parsing on replay).</summary>
    public required string RawPayload { get; init; }

    /// <summary>Verified signature header (kept for audit, not for re-verification on replay).</summary>
    public string? Signature { get; init; }

    /// <summary>UTC timestamp the SDK received the webhook.</summary>
    public required DateTime ReceivedAt { get; init; }

    /// <summary>Current handler outcome.</summary>
    public WebhookHandlerStatus Status { get; init; } = WebhookHandlerStatus.Pending;

    /// <summary>If <see cref="Status"/> is <see cref="WebhookHandlerStatus.Failed"/>, the captured reason.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Number of replay attempts so far.</summary>
    public int Attempts { get; init; }
}

/// <summary>Outcome state of a downstream webhook handler.</summary>
public enum WebhookHandlerStatus
{
    /// <summary>Received and persisted but not yet handed to the downstream handler.</summary>
    Pending,
    /// <summary>Handler succeeded.</summary>
    Handled,
    /// <summary>Handler raised an exception and the envelope is eligible for replay.</summary>
    Failed
}
