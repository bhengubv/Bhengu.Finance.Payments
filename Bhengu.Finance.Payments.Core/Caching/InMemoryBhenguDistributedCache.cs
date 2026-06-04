// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;

namespace Bhengu.Finance.Payments.Core.Caching;

/// <summary>
/// Process-local default for <see cref="IBhenguDistributedCache"/>. Stores the original object
/// reference (no JSON round-trip), so concurrent callers that hit the same key observe
/// reference-equal results — the contract that the in-flight coalescing layer in provider
/// caches depends on for effectively-exactly-once semantics within a single process.
///
/// <para>The Redis-backed implementation in <c>Bhengu.Finance.Payments.Redis</c> DOES JSON-serialise
/// (Redis is byte-oriented). Cross-process consumers must not assume reference equality.
/// Within a single process the in-memory default guarantees it.</para>
///
/// <para><b>Mutation note:</b> because values are stored by reference, mutating a stored object
/// AFTER <c>SetAsync</c> WILL leak through subsequent <c>GetAsync</c> calls. Every DTO in
/// <c>Bhengu.Finance.Payments.Core.Models</c> is a <c>record</c> (immutable by construction), so
/// this is a non-issue for the SDK's own use. Consumers caching their own mutable types should
/// be aware and prefer immutable records.</para>
///
/// <para>Suitable for single-replica deployments and tests. For multi-replica, install
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
        return Task.FromResult(entry.Value as T);
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        var entry = new Entry(value, DateTime.UtcNow.Add(ttl));
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

    private sealed record Entry(object Value, DateTime ExpiresAt);
}
