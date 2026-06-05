// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.Core.Webhooks;

/// <summary>
/// Default <see cref="IWebhookEventDispatcher"/>. Verifies signature, parses event, looks up the
/// provider's event id, records the envelope to <see cref="IWebhookReplayStore"/> for dedup,
/// invokes the handler once, marks the store on outcome.
/// </summary>
public sealed class WebhookEventDispatcher : IWebhookEventDispatcher
{
    private readonly IEnumerable<IPaymentGatewayProvider> _providers;
    private readonly IWebhookReplayStore _replayStore;
    private readonly ILogger<WebhookEventDispatcher> _logger;

    /// <summary>Construct the dispatcher.</summary>
    public WebhookEventDispatcher(
        IEnumerable<IPaymentGatewayProvider> providers,
        IWebhookReplayStore replayStore,
        ILogger<WebhookEventDispatcher> logger)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _replayStore = replayStore ?? throw new ArgumentNullException(nameof(replayStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<WebhookDispatchOutcome> DispatchAsync(
        string providerName,
        string rawPayload,
        string signatureHeader,
        Func<WebhookEvent, CancellationToken, Task> handler,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerName);
        ArgumentException.ThrowIfNullOrEmpty(rawPayload);
        ArgumentNullException.ThrowIfNull(handler);

        var provider = _providers.FirstOrDefault(p =>
            string.Equals(p.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            _logger.LogWarning("Webhook for unknown provider {Provider}", providerName);
            return WebhookDispatchOutcome.UnknownProvider;
        }

        if (!provider.VerifyWebhookSignature(rawPayload, signatureHeader ?? string.Empty))
        {
            _logger.LogWarning("Webhook signature verification failed for provider {Provider}", providerName);
            return WebhookDispatchOutcome.InvalidSignature;
        }

        var evt = await provider.ParseWebhookAsync(rawPayload, ct).ConfigureAwait(false);
        if (evt is null)
            return WebhookDispatchOutcome.UnknownEvent;

        // Use GatewayReference as the dedup id when the parsed event doesn't carry a richer one.
        // Providers that need stronger dedup semantics should subclass WebhookEvent with their
        // own EventId discriminator; for now GatewayReference is the most stable identifier
        // every provider populates.
        var envelope = new WebhookEnvelope
        {
            ProviderName = providerName,
            EventId = evt.GatewayReference,
            RawPayload = rawPayload,
            Signature = signatureHeader,
            ReceivedAt = DateTime.UtcNow,
        };

        var firstSeen = await _replayStore.TryRecordAsync(envelope, ct).ConfigureAwait(false);
        if (!firstSeen)
            return WebhookDispatchOutcome.Duplicate;

        try
        {
            await handler(evt, ct).ConfigureAwait(false);
            await _replayStore.MarkHandledAsync(envelope.EventId, ct).ConfigureAwait(false);
            return WebhookDispatchOutcome.Handled;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Webhook handler threw for provider {Provider} event {EventId}",
                providerName, envelope.EventId);
            await _replayStore.MarkFailedAsync(envelope.EventId, ex.Message, ct).ConfigureAwait(false);
            return WebhookDispatchOutcome.HandlerFailed;
        }
    }
}
