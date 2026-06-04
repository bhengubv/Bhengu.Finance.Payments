// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Redis.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Bhengu.Finance.Payments.Redis;

/// <summary>
/// DI registration helpers for the Redis-backed <see cref="IBhenguDistributedCache"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Redis-backed cache as the active <see cref="IBhenguDistributedCache"/>.
    /// Binds <see cref="RedisCacheOptions"/> from <see cref="RedisCacheOptions.ConfigSection"/>
    /// (or pass <paramref name="configSectionName"/> to bind from a non-default section), registers
    /// <see cref="IConnectionMultiplexer"/> as a singleton, and REPLACES any prior in-memory
    /// <see cref="IBhenguDistributedCache"/> registration so a single startup call swaps the cache.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration root.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddBhenguRedisCache(this IServiceCollection services, IConfiguration configuration)
        => AddBhenguRedisCache(services, configuration, RedisCacheOptions.ConfigSection);

    /// <summary>
    /// Register the Redis-backed cache, binding options from a non-default section name. Use this
    /// when migrating from a legacy configuration layout without renaming production keys.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="configSectionName">The configuration section binding path.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddBhenguRedisCache(
        this IServiceCollection services,
        IConfiguration configuration,
        string configSectionName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configSectionName);

        var section = configuration.GetSection(configSectionName);
        services.Configure<RedisCacheOptions>(section);

        var probe = section.Get<RedisCacheOptions>() ?? new RedisCacheOptions();

        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(probe.ConnectionString));

        // Replace any prior IBhenguDistributedCache registration (the in-memory default registered
        // by the provider Add*Payments extensions). A single AddBhenguRedisCache call must swap the
        // cache implementation outright; TryAddSingleton would leave the in-memory default winning.
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(IBhenguDistributedCache));
        if (existing is not null)
            services.Remove(existing);

        services.AddSingleton<IBhenguDistributedCache, RedisBhenguDistributedCache>();

        return services;
    }
}
