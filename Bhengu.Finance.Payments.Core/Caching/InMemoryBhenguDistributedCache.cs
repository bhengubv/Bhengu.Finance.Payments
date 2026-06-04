// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using System.Text.Json;

namespace Bhengu.Finance.Payments.Core.Caching;

/// <summary>
/// Process-local default for <see cref="IBhenguDistributedCache"/>. Backed by a thread-safe
/// dictionary; values are JSON-serialised on the way in so that consumers behave identically
/// against this and a real distributed cache (no shared mutable reference surprises).
///
/// <para>Suitable for single-replica deployments. For multi-replica, install
/// <c>Bhengu.Finance.Payments.Redis</c>.</para>
/// </summary>
public sealed class InMemoryBhenguDistributedCache : IBhenguDistributedCache
{
    private readonly ConcurrentDictionary<string, Entry> _store = new();

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (!_store.TryGetValue(key, out var entry))
            return Task.FromResult<T?>(null);
        if (entry.ExpiresAt <= DateTime.UtcNow)
        {
            _store.TryRemove(key, out _);
            return Task.FromResult<T?>(null);
        }
        return Task.FromResult(JsonSerializer.Deserialize<T>(entry.Json));
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        var json = JsonSerializer.Serialize(value);
        var entry = new Entry(json, DateTime.UtcNow.Add(ttl));
        _store[key] = entry;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    private sealed record Entry(string Json, DateTime ExpiresAt);
}
