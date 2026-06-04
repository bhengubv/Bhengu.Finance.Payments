// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using Bhengu.Finance.Payments.Core.Caching;

namespace Bhengu.Finance.Payments.PayUIndia.Internals;

/// <summary>
/// Client-side idempotency-key deduplication helper for the PayU India provider.
/// PayU India does NOT expose a native idempotency-key header on its REST endpoints, so this cache
/// implements the contract the Bhengu Core surface promises by coalescing concurrent and repeat
/// calls that share the same caller-supplied <c>IdempotencyKey</c> against the same in-flight or
/// completed result.
/// </summary>
/// <remarks>
/// <para>Backed by <see cref="IBhenguDistributedCache"/> so the dedupe survives process restarts and
/// works across replicas when a Redis cache is installed
/// (<c>Bhengu.Finance.Payments.Redis.AddBhenguRedisCache</c>); falls back to the process-local
/// <see cref="InMemoryBhenguDistributedCache"/> for single-instance deployments.</para>
/// <para>Concurrent in-flight calls within a single process are coalesced onto one
/// <see cref="Task{TResult}"/> so a burst of identical retries does not produce N redundant API
/// calls; only the first completes and its result is shared with peers and then committed to the
/// distributed store with a 24h TTL.</para>
/// </remarks>
public sealed class PayUIndiaIdempotencyCache
{
    private const string KeyPrefix = "payuindia:idempotency:";
    private static readonly TimeSpan TimeToLive = TimeSpan.FromHours(24);

    private readonly IBhenguDistributedCache _cache;
    private readonly ConcurrentDictionary<string, object> _inFlight = new(StringComparer.Ordinal);

    /// <summary>Construct with an injected distributed cache.</summary>
    public PayUIndiaIdempotencyCache(IBhenguDistributedCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>Default-constructor convenience for tests and back-compat callers.</summary>
    public PayUIndiaIdempotencyCache() : this(new InMemoryBhenguDistributedCache()) { }

    /// <summary>
    /// Execute <paramref name="factory"/> at most once for the given <paramref name="idempotencyKey"/>.
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

        var task = (Task<T>)_inFlight.GetOrAdd(idempotencyKey, _ => RunAndPersistAsync(factory, cacheKey, idempotencyKey));
        return await task.ConfigureAwait(false);
    }

    private async Task<T> RunAndPersistAsync<T>(Func<Task<T>> factory, string cacheKey, string idempotencyKey) where T : class
    {
        try
        {
            var result = await factory().ConfigureAwait(false);
            if (result is not null)
                await _cache.SetAsync(cacheKey, result, TimeToLive).ConfigureAwait(false);
            return result!;
        }
        finally
        {
            _inFlight.TryRemove(idempotencyKey, out _);
        }
    }
}
