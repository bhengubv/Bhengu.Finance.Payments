// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using Bhengu.Finance.Payments.Core.Caching;

namespace Bhengu.Finance.Payments.Flutterwave.Internals;

/// <summary>
/// Client-side idempotency cache for the Flutterwave provider family.
/// <para>
/// Flutterwave's v3 REST API does <b>not</b> expose a native <c>Idempotency-Key</c> header (unlike
/// Stripe). To honour caller-supplied <c>IdempotencyKey</c> values and offer at-least-once-safe
/// retry semantics, we coalesce concurrent calls bearing the same key onto a single in-flight
/// <see cref="Task{TResult}"/> and serve subsequent identical retries from a distributed cache
/// (<see cref="IBhenguDistributedCache"/>) keyed by <c>flutterwave:idempotency:{key}</c>.
/// </para>
/// <para>
/// Backed by <see cref="InMemoryBhenguDistributedCache"/> in single-instance deployments and by
/// Redis when the Redis package is installed — survives restarts and multi-replica setups for
/// 24 hours per key.
/// </para>
/// <para>
/// Faulted tasks are evicted from the in-flight map so a transient failure does not poison
/// subsequent retries; the distributed entry is only written on successful completion.
/// </para>
/// </summary>
/// <remarks>
/// Internal type — exposed for cross-provider re-use within the Flutterwave assembly only.
/// </remarks>
public sealed class FlutterwaveIdempotencyCache
{
    private const string KeyPrefix = "flutterwave:idempotency:";
    private static readonly TimeSpan TimeToLive = TimeSpan.FromHours(24);

    private readonly IBhenguDistributedCache _cache;
    private readonly ConcurrentDictionary<string, object> _entries = new(StringComparer.Ordinal);

    /// <summary>Construct with an injected distributed cache. Used in DI-driven scenarios.</summary>
    public FlutterwaveIdempotencyCache(IBhenguDistributedCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>Default-constructor convenience for tests and back-compat callers.</summary>
    public FlutterwaveIdempotencyCache() : this(new InMemoryBhenguDistributedCache()) { }

    /// <summary>
    /// Execute <paramref name="factory"/> exactly once per <paramref name="idempotencyKey"/>, returning
    /// the cached result on subsequent calls. When <paramref name="idempotencyKey"/> is null or empty
    /// the factory is invoked directly with no caching.
    /// </summary>
    public async Task<T> GetOrAddAsync<T>(string? idempotencyKey, Func<Task<T>> factory) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return await factory().ConfigureAwait(false);

        var cacheKey = KeyPrefix + idempotencyKey;

        var cached = await _cache.GetAsync<T>(cacheKey).ConfigureAwait(false);
        if (cached is not null)
            return cached;

        var task = (Task<T>)_entries.GetOrAdd(idempotencyKey, _ => RunAndPersistAsync(factory, cacheKey));
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            // Evict the in-flight entry once the task has finished (success or fault). The persisted
            // distributed-cache write happens inside RunAndPersistAsync on success, so subsequent
            // callers either hit the cache or get a fresh factory invocation.
            _entries.TryRemove(new KeyValuePair<string, object>(idempotencyKey, task));
        }
    }

    private async Task<T> RunAndPersistAsync<T>(Func<Task<T>> factory, string cacheKey) where T : class
    {
        var result = await factory().ConfigureAwait(false);
        if (result is not null)
            await _cache.SetAsync(cacheKey, result, TimeToLive).ConfigureAwait(false);
        return result!;
    }

    /// <summary>Remove every in-flight entry. Intended for tests; production code should not call this.</summary>
    public void Clear() => _entries.Clear();
}
