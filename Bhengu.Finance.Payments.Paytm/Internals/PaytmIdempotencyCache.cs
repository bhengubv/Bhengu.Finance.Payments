// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using Bhengu.Finance.Payments.Core.Caching;

namespace Bhengu.Finance.Payments.Paytm.Internals;

/// <summary>
/// Client-side idempotency-key deduplication helper for the Paytm provider.
/// Paytm does NOT expose a native idempotency-key header on its REST endpoints, so this cache
/// implements the contract the Bhengu Core surface promises.
/// </summary>
public sealed class PaytmIdempotencyCache
{
    private const string KeyPrefix = "paytm:idempotency:";
    private static readonly TimeSpan TimeToLive = TimeSpan.FromHours(24);

    private readonly IBhenguDistributedCache _cache;
    private readonly ConcurrentDictionary<string, object> _inFlight = new(StringComparer.Ordinal);

    /// <summary>Construct with an injected distributed cache.</summary>
    public PaytmIdempotencyCache(IBhenguDistributedCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>Default-constructor convenience for tests.</summary>
    public PaytmIdempotencyCache() : this(new InMemoryBhenguDistributedCache()) { }

    /// <summary>Execute <paramref name="factory"/> at most once for the given <paramref name="idempotencyKey"/>.</summary>
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
