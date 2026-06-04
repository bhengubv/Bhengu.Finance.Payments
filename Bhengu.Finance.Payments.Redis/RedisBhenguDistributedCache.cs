// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text.Json;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Redis.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Bhengu.Finance.Payments.Redis;

/// <summary>
/// Redis-backed <see cref="IBhenguDistributedCache"/>. Delegates to <see cref="IConnectionMultiplexer"/>
/// and JSON-serialises values via <see cref="System.Text.Json.JsonSerializer"/> so the wire format
/// matches what <see cref="InMemoryBhenguDistributedCache"/> would round-trip; consumers can swap
/// between the two without observable behaviour change.
///
/// <para>Keys are prefixed with <see cref="RedisCacheOptions.KeyPrefix"/> so multiple SDKs sharing
/// the same Redis instance don't collide.</para>
/// </summary>
public sealed class RedisBhenguDistributedCache : IBhenguDistributedCache
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly string _keyPrefix;

    /// <summary>Construct the cache bound to the given multiplexer and options.</summary>
    /// <param name="multiplexer">A live <see cref="IConnectionMultiplexer"/>; typically registered as a singleton in DI.</param>
    /// <param name="options">Cache options (key prefix etc.).</param>
    public RedisBhenguDistributedCache(IConnectionMultiplexer multiplexer, IOptions<RedisCacheOptions> options)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        ArgumentNullException.ThrowIfNull(options);
        _keyPrefix = options.Value?.KeyPrefix ?? string.Empty;
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ct.ThrowIfCancellationRequested();

        var db = _multiplexer.GetDatabase();
        var value = await db.StringGetAsync(BuildKey(key)).ConfigureAwait(false);
        if (value.IsNullOrEmpty)
            return null;

        return JsonSerializer.Deserialize<T>((string)value!);
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        ct.ThrowIfCancellationRequested();

        var json = JsonSerializer.Serialize(value);
        var db = _multiplexer.GetDatabase();
        await db.StringSetAsync(BuildKey(key), json, ttl).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ct.ThrowIfCancellationRequested();

        var db = _multiplexer.GetDatabase();
        await db.KeyDeleteAsync(BuildKey(key)).ConfigureAwait(false);
    }

    private string BuildKey(string key) => _keyPrefix + key;
}
