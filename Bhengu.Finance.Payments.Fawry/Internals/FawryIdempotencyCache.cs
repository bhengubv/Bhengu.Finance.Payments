// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using Bhengu.Finance.Payments.Core.Caching;

namespace Bhengu.Finance.Payments.Fawry.Internals;

/// <summary>
/// Idempotency-key deduplication helper for Fawry operations. Fawry does NOT expose a native
/// idempotency header — this cache implements the contract the Bhengu Core surface promises by
/// coalescing concurrent and repeat calls with the same caller-supplied <c>IdempotencyKey</c>.
/// </summary>
/// <remarks>
/// <para>Backed by an injected <see cref="IBhenguDistributedCache"/> for cross-process result
/// memoisation (24-hour TTL by default) plus an in-process <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// that coalesces concurrent in-flight calls.</para>
/// </remarks>
public sealed class FawryIdempotencyCache
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);
    private readonly IBhenguDistributedCache _cache;
    private readonly ConcurrentDictionary<string, object> _inFlight = new(StringComparer.Ordinal);

    /// <summary>Construct with the supplied distributed cache backend.</summary>
    public FawryIdempotencyCache(IBhenguDistributedCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>
    /// Execute <paramref name="factory"/> at most once for the given <paramref name="idempotencyKey"/>.
    /// </summary>
    public async Task<T> GetOrAddAsync<T>(
        string? idempotencyKey,
        string operation,
        Func<Task<T>> factory,
        CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return await factory().ConfigureAwait(false);

        var cacheKey = $"fawry:idemp:{operation}:{idempotencyKey}";

        var cached = await _cache.GetAsync<T>(cacheKey, ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        var task = (Task<T>)_inFlight.GetOrAdd(cacheKey, _ => InvokeAndCacheAsync(cacheKey, factory, ct));
        try { return await task.ConfigureAwait(false); }
        finally { _inFlight.TryRemove(cacheKey, out _); }
    }

    private async Task<T> InvokeAndCacheAsync<T>(string cacheKey, Func<Task<T>> factory, CancellationToken ct) where T : class
    {
        var result = await factory().ConfigureAwait(false);
        await _cache.SetAsync(cacheKey, result, DefaultTtl, ct).ConfigureAwait(false);
        return result;
    }
}
