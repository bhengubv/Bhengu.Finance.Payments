// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using Bhengu.Finance.Payments.Core.Caching;

namespace Bhengu.Finance.Payments.MTNMoMo.Internals;

/// <summary>
/// MTN MoMo OAuth token cache backed by <see cref="IBhenguDistributedCache"/>. MoMo issues
/// distinct tokens per product (collection vs disbursement) so the cache key is scoped by product
/// AND by API-user id; with a Redis-backed implementation every replica shares each product's
/// token until <c>expires_in − 60s</c> instead of each replica fetching its own.
/// </summary>
/// <remarks>
/// <para>Concurrent fetches for the same (product, apiUserId) inside one process are coalesced onto
/// one <see cref="Task{TResult}"/> via a <c>Lazy&lt;Task&lt;string&gt;&gt;</c> guarded in a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>; only the first caller hits the MoMo token
/// endpoint, peers observe the same result. <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/>
/// is critical so the dict's value factory does not run multiple times during the burst.</para>
/// </remarks>
public sealed class MTNMoMoOAuthCache
{
    private const string KeyPrefix = "mtnmomo:oauth:";

    private readonly IBhenguDistributedCache _cache;
    private readonly ConcurrentDictionary<string, object> _inFlight = new(StringComparer.Ordinal);

    public MTNMoMoOAuthCache(IBhenguDistributedCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>
    /// Default-constructor convenience for tests / back-compat callers without DI. Uses a private
    /// <see cref="InMemoryBhenguDistributedCache"/>.
    /// </summary>
    public MTNMoMoOAuthCache() : this(new InMemoryBhenguDistributedCache()) { }

    /// <summary>
    /// Return a valid MoMo access token for the given <paramref name="product"/> + <paramref name="apiUserId"/>.
    /// Cache hits return immediately; concurrent misses share one <paramref name="fetch"/> call,
    /// after which the token is cached for <c>(expiresIn − 60s)</c> seconds.
    /// </summary>
    /// <param name="product">MoMo product — typically <c>"collection"</c> or <c>"disbursement"</c>.</param>
    /// <param name="apiUserId">API user id (per product).</param>
    /// <param name="fetch">Async factory performing the actual <c>{product}/token/</c> POST and
    /// returning <c>(accessToken, expiresInSeconds)</c>.</param>
    /// <param name="ct">Cancellation token forwarded to the cache and factory.</param>
    public async Task<string> GetOrFetchAsync(
        string product,
        string apiUserId,
        Func<CancellationToken, Task<(string AccessToken, int ExpiresInSeconds)>> fetch,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(product);
        ArgumentException.ThrowIfNullOrEmpty(apiUserId);
        ArgumentNullException.ThrowIfNull(fetch);

        var cacheKey = BuildKey(product, apiUserId);

        var existing = await _cache.GetAsync<TokenEntry>(cacheKey, ct).ConfigureAwait(false);
        if (existing is not null && !string.IsNullOrEmpty(existing.AccessToken))
            return existing.AccessToken;

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
            _inFlight.TryRemove(new KeyValuePair<string, object>(cacheKey, lazy));
        }
    }

    private async Task<string> RunAndPersistAsync(
        string cacheKey,
        Func<CancellationToken, Task<(string AccessToken, int ExpiresInSeconds)>> fetch,
        CancellationToken ct)
    {
        // Re-check inside the coalesced run — another replica may have landed the token between our
        // initial miss and our turn.
        var snapshot = await _cache.GetAsync<TokenEntry>(cacheKey, ct).ConfigureAwait(false);
        if (snapshot is not null && !string.IsNullOrEmpty(snapshot.AccessToken))
            return snapshot.AccessToken;

        var (token, expiresIn) = await fetch(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
            return token;

        var ttl = TimeSpan.FromSeconds(Math.Max(60, expiresIn - 60));
        await _cache.SetAsync(cacheKey, new TokenEntry { AccessToken = token }, ttl, ct).ConfigureAwait(false);
        return token;
    }

    private static string BuildKey(string product, string apiUserId) =>
        $"{KeyPrefix}{product}:" + (apiUserId.Length > 8 ? apiUserId[^8..] : apiUserId);

    /// <summary>Serialised cache entry — record for safe in-memory reference storage.</summary>
    public sealed record TokenEntry
    {
        public string AccessToken { get; init; } = string.Empty;
    }
}
