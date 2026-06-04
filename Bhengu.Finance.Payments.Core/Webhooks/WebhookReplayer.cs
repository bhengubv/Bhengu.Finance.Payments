// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.Core.Webhooks;

/// <summary>
/// Default <see cref="IWebhookReplayer"/>. Iterates pending envelopes from the store, re-parses
/// each via the relevant <see cref="IPaymentGatewayProvider"/>, and invokes the supplied handler.
/// Failures are caught per-envelope so a poison message doesn't stall the rest.
/// </summary>
public sealed class WebhookReplayer : IWebhookReplayer
{
    private readonly IWebhookReplayStore _store;
    private readonly IEnumerable<IPaymentGatewayProvider> _providers;
    private readonly ILogger<WebhookReplayer> _logger;

    /// <summary>Construct the replayer with its dependencies.</summary>
    public WebhookReplayer(
        IWebhookReplayStore store,
        IEnumerable<IPaymentGatewayProvider> providers,
        ILogger<WebhookReplayer> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<int> ReplayAsync(
        string providerName,
        Func<WebhookEvent, CancellationToken, Task> handler,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerName);
        ArgumentNullException.ThrowIfNull(handler);

        var provider = _providers.FirstOrDefault(p =>
            string.Equals(p.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            _logger.LogWarning("WebhookReplayer: no provider registered for {Provider}", providerName);
            return 0;
        }

        var pending = await _store.ListPendingAsync(providerName, ct: ct).ConfigureAwait(false);
        var successCount = 0;

        foreach (var envelope in pending)
        {
            try
            {
                var evt = await provider.ParseWebhookAsync(envelope.RawPayload, ct).ConfigureAwait(false);
                if (evt is null)
                {
                    await _store.MarkFailedAsync(envelope.EventId, "ParseWebhookAsync returned null", ct).ConfigureAwait(false);
                    continue;
                }

                await handler(evt, ct).ConfigureAwait(false);
                await _store.MarkHandledAsync(envelope.EventId, ct).ConfigureAwait(false);
                successCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WebhookReplayer: handler failed for {Provider} event {EventId} (attempt {Attempts})",
                    providerName, envelope.EventId, envelope.Attempts + 1);
                await _store.MarkFailedAsync(envelope.EventId, ex.Message, ct).ConfigureAwait(false);
            }
        }

        return successCount;
    }
}
