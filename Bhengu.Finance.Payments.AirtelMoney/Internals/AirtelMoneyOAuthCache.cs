// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Caching;

namespace Bhengu.Finance.Payments.AirtelMoney.Internals;

/// <summary>
/// Distributed-cache-backed Airtel Money OAuth token cache. Survives process restarts and is
/// shared across replicas when Redis is wired up via <c>Bhengu.Finance.Payments.Redis</c>.
/// A per-instance <see cref="SemaphoreSlim"/> deduplicates concurrent token fetches within a
/// single replica; the cache layer prevents redundant fetches across replicas / restarts.
/// </summary>
internal sealed class AirtelMoneyOAuthCache
{
    private const string KeyPrefix = "airtel:oauth:";

    private readonly IBhenguDistributedCache _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AirtelMoneyOAuthCache(IBhenguDistributedCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>Try to read a cached token for the given client id. Returns null when missing / expired.</summary>
    public async Task<string?> GetAsync(string clientId, CancellationToken ct)
    {
        var entry = await _cache.GetAsync<TokenEntry>(BuildKey(clientId), ct).ConfigureAwait(false);
        return entry?.AccessToken;
    }

    /// <summary>Store a token under the given client id for at most <paramref name="ttl"/>.</summary>
    public Task SetAsync(string clientId, string accessToken, TimeSpan ttl, CancellationToken ct) =>
        _cache.SetAsync(BuildKey(clientId), new TokenEntry { AccessToken = accessToken }, ttl, ct);

    /// <summary>Acquire the per-instance fetch gate.</summary>
    public Task WaitFetchSlotAsync(CancellationToken ct) => _gate.WaitAsync(ct);

    /// <summary>Release the per-instance fetch gate.</summary>
    public void ReleaseFetchSlot() => _gate.Release();

    private static string BuildKey(string clientId) =>
        KeyPrefix + (clientId.Length > 8 ? clientId[^8..] : clientId);

    /// <summary>Serialised cache entry.</summary>
    public sealed record TokenEntry
    {
        public string AccessToken { get; init; } = string.Empty;
    }
}
