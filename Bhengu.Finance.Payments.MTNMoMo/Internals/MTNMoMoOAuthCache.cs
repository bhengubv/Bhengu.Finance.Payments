// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Caching;

namespace Bhengu.Finance.Payments.MTNMoMo.Internals;

/// <summary>
/// Distributed-cache-backed MoMo OAuth token cache, keyed per product (collection vs disbursement).
/// MoMo issues distinct tokens for each product API, so the cache scopes by product name. A
/// per-instance <see cref="SemaphoreSlim"/> deduplicates concurrent token fetches within a single
/// replica; the cache layer prevents redundant fetches across replicas / restarts.
/// </summary>
internal sealed class MTNMoMoOAuthCache
{
    private readonly IBhenguDistributedCache _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MTNMoMoOAuthCache(IBhenguDistributedCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>Try to read a cached token for the given product / API-user key. Returns null when missing / expired.</summary>
    public async Task<string?> GetAsync(string product, string apiUserId, CancellationToken ct)
    {
        var entry = await _cache.GetAsync<TokenEntry>(BuildKey(product, apiUserId), ct).ConfigureAwait(false);
        return entry?.AccessToken;
    }

    /// <summary>Store a token under the given product / API-user key for at most <paramref name="ttl"/>.</summary>
    public Task SetAsync(string product, string apiUserId, string accessToken, TimeSpan ttl, CancellationToken ct) =>
        _cache.SetAsync(BuildKey(product, apiUserId), new TokenEntry { AccessToken = accessToken }, ttl, ct);

    /// <summary>Acquire the per-instance fetch gate.</summary>
    public Task WaitFetchSlotAsync(CancellationToken ct) => _gate.WaitAsync(ct);

    /// <summary>Release the per-instance fetch gate.</summary>
    public void ReleaseFetchSlot() => _gate.Release();

    private static string BuildKey(string product, string apiUserId) =>
        $"mtnmomo:{product}-oauth:" + (apiUserId.Length > 8 ? apiUserId[^8..] : apiUserId);

    /// <summary>Serialised cache entry.</summary>
    public sealed record TokenEntry
    {
        public string AccessToken { get; init; } = string.Empty;
    }
}
