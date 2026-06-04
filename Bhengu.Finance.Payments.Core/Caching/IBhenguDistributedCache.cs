// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Caching;

/// <summary>
/// Distributed-cache abstraction used by providers that need cross-process state — idempotency-key
/// dedup caches (Paystack, Flutterwave, PayFast, Yoco), in-process plan/split definition stores
/// (Stripe Marketplace, Flutterwave, PayFast plans, MercadoPago/PagSeguro Preapproval), and the
/// Yoco token cache.
///
/// <para>The default registration is <see cref="InMemoryBhenguDistributedCache"/> (process-local),
/// which keeps single-instance deployments working without configuration. For multi-replica deploys,
/// install <c>Bhengu.Finance.Payments.Redis</c> and call <c>AddBhenguRedisCache(connectionString)</c>
/// to substitute a Redis-backed implementation that survives restarts and works across nodes.</para>
///
/// <para>The contract is intentionally tiny: get / set with TTL / remove. Caches that need richer
/// semantics (e.g. atomic increment) should use a different abstraction.</para>
/// </summary>
public interface IBhenguDistributedCache
{
    /// <summary>
    /// Fetch a value by key. Returns null if the key has expired or was never set. Values are
    /// serialised as UTF-8 JSON by the implementation.
    /// </summary>
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class;

    /// <summary>
    /// Store a value under a key. <paramref name="ttl"/> bounds how long the value is honoured;
    /// implementations may evict sooner under memory pressure.
    /// </summary>
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) where T : class;

    /// <summary>Delete a key. No-op if the key isn't present.</summary>
    Task RemoveAsync(string key, CancellationToken ct = default);
}
