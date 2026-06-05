// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using Bhengu.Finance.Payments.Core.Caching;

namespace Bhengu.Finance.Payments.MPesa.Internals;

/// <summary>
/// Daraja OAuth token cache backed by <see cref="IBhenguDistributedCache"/>. In a multi-replica
/// deployment with a Redis-backed cache (<c>Bhengu.Finance.Payments.Redis.AddBhenguRedisCache</c>)
/// every replica shares the same token until <c>expires_in − 60s</c>, so the upstream OAuth
/// endpoint is hit at most once per token lifetime per consumer-key — not once per replica.
/// </summary>
/// <remarks>
/// <para>Concurrent fetches for the same consumer key inside a single process are coalesced onto
/// one <see cref="Task{TResult}"/> via a <c>Lazy&lt;Task&lt;string&gt;&gt;</c> guarded in a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>; only the first caller hits Daraja, peers
/// observe the same result. Once that task completes the token is committed to the distributed
/// store and the in-flight slot is evicted so the next fetch (after TTL expiry) starts a new burst.</para>
/// <para>The <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> mode is critical — a plain
/// <c>ConcurrentDictionary.GetOrAdd</c> value factory can fire multiple times before one thread
/// wins the insert, burning N redundant OAuth calls before the dedupe takes effect.</para>
/// </remarks>
public sealed class MPesaOAuthCache
{
    private const string KeyPrefix = "mpesa:oauth:";

    private readonly IBhenguDistributedCache _cache;
    private readonly ConcurrentDictionary<string, object> _inFlight = new(StringComparer.Ordinal);

    public MPesaOAuthCache(IBhenguDistributedCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>
    /// Default-constructor convenience for tests / back-compat callers without DI. Uses a private
    /// <see cref="InMemoryBhenguDistributedCache"/>.
    /// </summary>
    public MPesaOAuthCache() : this(new InMemoryBhenguDistributedCache()) { }

    /// <summary>
    /// Return a valid Daraja access token for <paramref name="consumerKey"/>. Cache hits return
    /// immediately. Concurrent misses are coalesced onto one <paramref name="fetch"/> invocation;
    /// after it completes the token is cached for <c>(expiresIn − 60s)</c> seconds.
    /// </summary>
    /// <param name="consumerKey">Daraja consumer key — used to derive the cache slot.</param>
    /// <param name="fetch">Async factory that performs the actual <c>oauth/v1/generate</c> call and
    /// returns <c>(accessToken, expiresInSeconds)</c>.</param>
    /// <param name="ct">Cancellation token forwarded to the cache and factory.</param>
    public async Task<string> GetOrFetchAsync(
        string consumerKey,
        Func<CancellationToken, Task<(string AccessToken, int ExpiresInSeconds)>> fetch,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(consumerKey);
        ArgumentNullException.ThrowIfNull(fetch);

        var cacheKey = BuildKey(consumerKey);

        // Distributed-cache hit short-circuits the in-flight machinery — the common case.
        var existing = await _cache.GetAsync<TokenEntry>(cacheKey, ct).ConfigureAwait(false);
        if (existing is not null && !string.IsNullOrEmpty(existing.AccessToken))
            return existing.AccessToken;

        // CRITICAL: Lazy<>(ExecutionAndPublication) so concurrent peers race to GetOrAdd but only
        // one Lazy.Value invocation actually runs the factory. Without Lazy, the dict's value
        // factory can fire multiple times.
        var lazy = (Lazy<Task<string>>)_inFlight.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<string>>(() => RunAndPersistAsync(cacheKey, fetch, ct),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            // Evict only the slot we added — survives concurrent peers using the same key.
            _inFlight.TryRemove(new KeyValuePair<string, object>(cacheKey, lazy));
        }
    }

    private async Task<string> RunAndPersistAsync(
        string cacheKey,
        Func<CancellationToken, Task<(string AccessToken, int ExpiresInSeconds)>> fetch,
        CancellationToken ct)
    {
        // Re-check the distributed cache once inside the coalesced run — another replica may have
        // landed the token between our miss and our turn.
        var snapshot = await _cache.GetAsync<TokenEntry>(cacheKey, ct).ConfigureAwait(false);
        if (snapshot is not null && !string.IsNullOrEmpty(snapshot.AccessToken))
            return snapshot.AccessToken;

        var (token, expiresIn) = await fetch(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
            return token;

        // Never exceed the upstream-supplied lifetime; trim 60s for clock-skew + handover safety.
        var ttl = TimeSpan.FromSeconds(Math.Max(60, expiresIn - 60));
        await _cache.SetAsync(cacheKey, new TokenEntry { AccessToken = token }, ttl, ct).ConfigureAwait(false);
        return token;
    }

    private static string BuildKey(string consumerKey) =>
        KeyPrefix + (consumerKey.Length > 8 ? consumerKey[^8..] : consumerKey);

    /// <summary>Serialised cache entry — record for safe in-memory reference storage.</summary>
    public sealed record TokenEntry
    {
        public string AccessToken { get; init; } = string.Empty;
    }
}
