// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Caching;

namespace Bhengu.Finance.Payments.Kashier.Internals;

/// <summary>
/// Client-side dedup wrapper for Kashier — Kashier's REST API accepts an <c>Idempotency-Key</c>
/// header on charge / refund / tokenisation, and this cache memoises the first response so a
/// retry that reaches the SDK never re-issues the call upstream.
/// </summary>
public sealed class KashierIdempotencyCache
{
    private readonly IBhenguDistributedCache _cache;
    private readonly TimeSpan _ttl;

    /// <summary>Construct an idempotency cache. TTL defaults to 24 hours.</summary>
    public KashierIdempotencyCache(IBhenguDistributedCache cache, TimeSpan? ttl = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _ttl = ttl ?? TimeSpan.FromHours(24);
    }

    /// <summary>Execute the factory at most once per idempotency key.</summary>
    public async Task<T> GetOrAddAsync<T>(string? idempotencyKey, Func<Task<T>> factory, CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return await factory().ConfigureAwait(false);

        var key = $"kashier:idem:{idempotencyKey}";
        var cached = await _cache.GetAsync<CachedResponse<T>>(key, ct).ConfigureAwait(false);
        if (cached is { Value: not null })
            return cached.Value;

        var value = await factory().ConfigureAwait(false);
        await _cache.SetAsync(key, new CachedResponse<T> { Value = value }, _ttl, ct).ConfigureAwait(false);
        return value;
    }

    /// <summary>JSON-friendly envelope so concrete records survive the cache's JSON round-trip.</summary>
    public sealed class CachedResponse<T> where T : class
    {
        /// <summary>The cached value.</summary>
        public T? Value { get; set; }
    }
}
