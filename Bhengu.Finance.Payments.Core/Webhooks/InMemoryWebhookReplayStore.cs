// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;

namespace Bhengu.Finance.Payments.Core.Webhooks;

/// <summary>
/// Process-local default for <see cref="IWebhookReplayStore"/>. Suitable for single-replica
/// deployments and tests; production multi-replica deployments should swap for a DB-backed
/// implementation (table with columns: event_id PK, provider, payload, status, attempts, ...).
/// </summary>
public sealed class InMemoryWebhookReplayStore : IWebhookReplayStore
{
    private readonly ConcurrentDictionary<string, WebhookEnvelope> _store = new();

    /// <inheritdoc />
    public Task<bool> TryRecordAsync(WebhookEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var added = _store.TryAdd(envelope.EventId, envelope);
        return Task.FromResult(added);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WebhookEnvelope>> ListPendingAsync(string providerName, int maxItems = 100, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerName);
        var pending = _store.Values
            .Where(e => e.ProviderName == providerName && e.Status != WebhookHandlerStatus.Handled)
            .OrderBy(e => e.ReceivedAt)
            .Take(maxItems)
            .ToList();
        return Task.FromResult<IReadOnlyList<WebhookEnvelope>>(pending);
    }

    /// <inheritdoc />
    public Task MarkHandledAsync(string eventId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventId);
        if (_store.TryGetValue(eventId, out var existing))
        {
            _store[eventId] = existing with { Status = WebhookHandlerStatus.Handled, FailureReason = null };
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MarkFailedAsync(string eventId, string failureReason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventId);
        if (_store.TryGetValue(eventId, out var existing))
        {
            _store[eventId] = existing with
            {
                Status = WebhookHandlerStatus.Failed,
                FailureReason = failureReason,
                Attempts = existing.Attempts + 1
            };
        }
        return Task.CompletedTask;
    }
}
