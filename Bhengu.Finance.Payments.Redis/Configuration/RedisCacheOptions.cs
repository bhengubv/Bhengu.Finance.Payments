// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Redis.Configuration;

/// <summary>
/// Configuration for the Redis-backed <see cref="Bhengu.Finance.Payments.Core.Caching.IBhenguDistributedCache"/>.
/// Bound from <see cref="ConfigSection"/> in <c>IConfiguration</c>.
/// </summary>
public sealed class RedisCacheOptions
{
    /// <summary>Configuration section binding path.</summary>
    public const string ConfigSection = "Bhengu:Finance:Payments:Redis";

    /// <summary>
    /// StackExchange.Redis connection string. Default targets a local development Redis.
    /// In production set this to your managed Redis endpoint (e.g. ElastiCache, Azure Cache, MemoryStore).
    /// </summary>
    public string ConnectionString { get; set; } = "localhost:6379";

    /// <summary>
    /// Prefix applied to every cache key so the Bhengu SDK doesn't collide with other tenants of
    /// the same Redis instance. Includes the trailing colon by convention.
    /// </summary>
    public string KeyPrefix { get; set; } = "bhengu:payments:";
}
