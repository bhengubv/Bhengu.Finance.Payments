// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using Bhengu.Finance.Payments.Core.Caching;

namespace Bhengu.Finance.Payments.Paystack.Internals;

/// <summary>
/// Client-side idempotency-key deduplication helper for the Paystack provider.
/// Paystack does NOT expose a native idempotency-key header — this cache implements the contract
/// the Bhengu Core surface promises by coalescing concurrent and repeat calls that share the same
/// caller-supplied <c>IdempotencyKey</c> against the same in-flight or completed result.
/// </summary>
/// <remarks>
/// <para>Backed by <see cref="IBhenguDistributedCache"/> so the dedupe survives process restarts
/// and works across replicas when a Redis cache is installed
/// (<c>Bhengu.Finance.Payments.Redis.AddBhenguRedisCache</c>); falls back to the process-local
/// <see cref="InMemoryBhenguDistributedCache"/> for single-instance deployments.</para>
/// <para>Concurrent in-flight calls within a single process are still coalesced onto one
/// <see cref="Task{TResult}"/> so a burst of identical retries does not produce N redundant API
/// calls; only the first completes and its result is shared with peers and then committed to the
/// distributed store with a 24h TTL.</para>
/// </remarks>
public sealed class PaystackIdempotencyCache
{
    private const string KeyPrefix = "paystack:idempotency:";
    private static readonly TimeSpan TimeToLive = TimeSpan.FromHours(24);

    private readonly IBhenguDistributedCache _cache;
    private readonly ConcurrentDictionary<string, object> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Construct with an injected distributed cache. The default <see cref="InMemoryBhenguDistributedCache"/>
    /// is registered automatically by <c>AddPaystackPayments</c> so single-instance callers do not
    /// have to do anything; multi-replica callers install the Redis package and re-register.
    /// </summary>
    public PaystackIdempotencyCache(IBhenguDistributedCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>
    /// Default-constructor convenience for tests and back-compat callers that do not have a DI
    /// container. Uses a private <see cref="InMemoryBhenguDistributedCache"/>.
    /// </summary>
    public PaystackIdempotencyCache() : this(new InMemoryBhenguDistributedCache()) { }

    /// <summary>
    /// Execute <paramref name="factory"/> at most once for the given <paramref name="idempotencyKey"/>.
    /// Concurrent in-flight callers share the same <see cref="Task{TResult}"/>; subsequent callers
    /// after the first one completes are served from the distributed cache for <c>24h</c>.
    /// </summary>
    /// <typeparam name="T">Result type of the factory.</typeparam>
    /// <param name="idempotencyKey">Caller-supplied dedupe token. Null or whitespace bypasses the cache.</param>
    /// <param name="factory">Async factory producing the result. Only invoked on cache miss.</param>
    /// <returns>The Task returned by the factory, possibly cached from a prior call.</returns>
    public async Task<T> GetOrAddAsync<T>(string? idempotencyKey, Func<Task<T>> factory) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return await factory().ConfigureAwait(false);

        var cacheKey = KeyPrefix + idempotencyKey;

        // In-flight FIRST so concurrent peers within the same burst share the same Task and observe
        // the same materialised result instance. Checking the distributed cache first would race —
        // caller 1 completes synchronously and persists; caller 2 then sees the cache hit and gets
        // a JSON-deserialised copy (different reference) instead of the coalesced Task result.
        // CRITICAL: wrap in Lazy<Task<T>>(ExecutionAndPublication) so the factory runs exactly once
        // even when N concurrent threads race into GetOrAdd. Without Lazy, ConcurrentDictionary's
        // value factory can fire multiple times before one thread wins the insert — burning N HTTP
        // calls before the dedupe takes effect.
        if (_inFlight.TryGetValue(idempotencyKey, out var existing))
        {
            return await ((Lazy<Task<T>>)existing).Value.ConfigureAwait(false);
        }

        // Now check the distributed cache for completed prior calls (different burst).
        var cached = await _cache.GetAsync<T>(cacheKey).ConfigureAwait(false);
        if (cached is not null)
            return cached;

        var lazy = (Lazy<Task<T>>)_inFlight.GetOrAdd(
            idempotencyKey,
            _ => new Lazy<Task<T>>(() => RunAndPersistAsync(factory, cacheKey),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            // Evict only the entry we added — survives concurrent peers using the same key.
            _inFlight.TryRemove(new KeyValuePair<string, object>(idempotencyKey, lazy));
        }
    }

    private async Task<T> RunAndPersistAsync<T>(Func<Task<T>> factory, string cacheKey) where T : class
    {
        var result = await factory().ConfigureAwait(false);
        if (result is not null)
            await _cache.SetAsync(cacheKey, result, TimeToLive).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Remove a cached entry. Use after a deliberate retry decision to allow a future call with the
    /// same key to reach the provider again. Most callers will not need this.
    /// </summary>
    /// <returns>
    /// True when an entry was evicted from either the in-flight map or the distributed cache;
    /// false only when nothing was cached under that key.
    /// </returns>
    public bool Invalidate(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        var inFlightRemoved = _inFlight.TryRemove(idempotencyKey, out _);
        // Sync-over-async on a process-local InMemoryBhenguDistributedCache is safe (it never blocks
        // on I/O); Redis-backed implementations should expose an InvalidateAsync overload — out of scope here.
        var cacheKey = KeyPrefix + idempotencyKey;
        var existed = _cache.GetAsync<object>(cacheKey).GetAwaiter().GetResult() is not null;
        _cache.RemoveAsync(cacheKey).GetAwaiter().GetResult();
        return inFlightRemoved || existed;
    }
}
