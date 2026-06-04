// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using Bhengu.Finance.Payments.Core.Caching;

namespace Bhengu.Finance.Payments.UnionPay.Internals;

/// <summary>
/// Client-side idempotency-key deduplication helper for the China UnionPay provider.
/// UnionPay's 5.1 gateway has no native idempotency-key header — this cache implements the
/// contract the Bhengu Core surface promises by coalescing concurrent and repeat calls that
/// share the same caller-supplied <c>IdempotencyKey</c> against the same in-flight or
/// completed result.
/// </summary>
public sealed class UnionPayIdempotencyCache
{
    private const string KeyPrefix = "unionpay:idempotency:";
    private static readonly TimeSpan TimeToLive = TimeSpan.FromHours(24);

    private readonly IBhenguDistributedCache _cache;
    private readonly ConcurrentDictionary<string, object> _inFlight = new(StringComparer.Ordinal);

    /// <summary>Construct with an injected distributed cache.</summary>
    public UnionPayIdempotencyCache(IBhenguDistributedCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>Default-constructor convenience for tests.</summary>
    public UnionPayIdempotencyCache() : this(new InMemoryBhenguDistributedCache()) { }

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

        var task = (Task<T>)_inFlight.GetOrAdd(idempotencyKey, _ => RunAndPersistAsync(factory, cacheKey));
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, object>(idempotencyKey, task));
        }
    }

    private async Task<T> RunAndPersistAsync<T>(Func<Task<T>> factory, string cacheKey) where T : class
    {
        var result = await factory().ConfigureAwait(false);
        if (result is not null)
            await _cache.SetAsync(cacheKey, result, TimeToLive).ConfigureAwait(false);
        return result!;
    }
}
