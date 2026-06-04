// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text.Json;
using Bhengu.Finance.Payments.Core.Caching;

namespace Bhengu.Finance.Payments.Paymob.Internals;

/// <summary>
/// Idempotency-key deduplication helper for the Paymob provider family. Paymob's Accept and
/// Disbursement APIs do NOT expose a native idempotency-key header — this cache implements the
/// contract that <see cref="Core.ProviderCapabilities.Idempotency"/> promises by storing the
/// first response under the caller-supplied key for a bounded TTL.
/// </summary>
/// <remarks>
/// Backed by <see cref="IBhenguDistributedCache"/>: in single-replica deployments this is
/// process-local; in multi-replica deployments wire the Redis backend to make the dedup window
/// cross-node. TTL defaults to 24 hours which matches Paymob's order-id retention window.
/// </remarks>
public sealed class PaymobIdempotencyCache
{
    private readonly IBhenguDistributedCache _cache;
    private readonly TimeSpan _ttl;

    /// <summary>
    /// Construct an idempotency cache. <paramref name="ttl"/> defaults to 24 hours when null.
    /// </summary>
    public PaymobIdempotencyCache(IBhenguDistributedCache cache, TimeSpan? ttl = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _ttl = ttl ?? TimeSpan.FromHours(24);
    }

    /// <summary>
    /// Execute <paramref name="factory"/> at most once per <paramref name="idempotencyKey"/>.
    /// When the key was seen before the cached result is returned without invoking the delegate.
    /// </summary>
    /// <remarks>
    /// When <paramref name="idempotencyKey"/> is null or whitespace the delegate is invoked with
    /// no deduplication — preserves prior behaviour for callers that have not opted in.
    /// </remarks>
    public async Task<T> GetOrAddAsync<T>(string? idempotencyKey, Func<Task<T>> factory, CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return await factory().ConfigureAwait(false);

        var key = $"paymob:idem:{idempotencyKey}";
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
