// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using Bhengu.Finance.Payments.Core.Caching;

namespace Bhengu.Finance.Payments.TymeBank.Internals;

/// <summary>
/// TymeBank OAuth2 client_credentials token cache backed by <see cref="IBhenguDistributedCache"/>.
/// Shared by <c>TymeBankPaymentProvider</c> (pay-by-bank, QR, payout) and
/// <c>TymeBankMandateProvider</c> (debit-order mandates) — both providers authenticate against the
/// same <c>oauth2/token</c> endpoint with the same client credentials, so cache hits cross provider
/// boundaries. With a Redis implementation every replica shares the same access token until
/// <c>expires_in − 60s</c>.
/// </summary>
/// <remarks>
/// <para>Concurrent fetches for the same client id within one process are coalesced onto a single
/// <see cref="Task{TResult}"/> via a <c>Lazy&lt;Task&lt;string&gt;&gt;</c> guarded in a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>; only the first caller hits TymeBank, peers
/// observe the same result. <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> is critical
/// so the dict's value factory does not run multiple times during the burst.</para>
/// </remarks>
public sealed class TymeBankOAuthCache
{
    private const string KeyPrefix = "tymebank:oauth:";

    private readonly IBhenguDistributedCache _cache;
    private readonly ConcurrentDictionary<string, object> _inFlight = new(StringComparer.Ordinal);

    public TymeBankOAuthCache(IBhenguDistributedCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>
    /// Default-constructor convenience for tests / back-compat callers without DI. Uses a private
    /// <see cref="InMemoryBhenguDistributedCache"/>.
    /// </summary>
    public TymeBankOAuthCache() : this(new InMemoryBhenguDistributedCache()) { }

    /// <summary>
    /// Return a valid TymeBank access token for <paramref name="clientId"/>. Cache hits return
    /// immediately; concurrent misses are coalesced onto one <paramref name="fetch"/> invocation,
    /// after which the token is cached for <c>(expiresIn − 60s)</c> seconds.
    /// </summary>
    /// <param name="clientId">TymeBank OAuth2 client id — used to derive the cache slot.</param>
    /// <param name="fetch">Async factory performing the actual <c>oauth2/token</c> POST and
    /// returning <c>(accessToken, expiresInSeconds)</c>.</param>
    /// <param name="ct">Cancellation token forwarded to the cache and factory.</param>
    public async Task<string> GetOrFetchAsync(
        string clientId,
        Func<CancellationToken, Task<(string AccessToken, int ExpiresInSeconds)>> fetch,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        ArgumentNullException.ThrowIfNull(fetch);

        var cacheKey = BuildKey(clientId);

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

    private static string BuildKey(string clientId) =>
        KeyPrefix + (clientId.Length > 8 ? clientId[^8..] : clientId);

    /// <summary>Serialised cache entry — record for safe in-memory reference storage.</summary>
    public sealed record TokenEntry
    {
        public string AccessToken { get; init; } = string.Empty;
    }
}
