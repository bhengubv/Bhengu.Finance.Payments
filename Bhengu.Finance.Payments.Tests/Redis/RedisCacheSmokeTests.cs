// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Redis;
using Bhengu.Finance.Payments.Redis.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Redis;

/// <summary>
/// Smoke tests for the Redis cache wrapper. Constructor wiring + DI registration are exercised
/// here without a live Redis instance; the actual set/get round-trip test is marked
/// <c>[Fact(Skip = ...)]</c> because CI doesn't run a Redis container.
/// </summary>
public class RedisCacheSmokeTests
{
    [Fact]
    public void Constructor_AcceptsValidMultiplexerAndOptions()
    {
        var mux = new Mock<IConnectionMultiplexer>().Object;
        var cache = new RedisBhenguDistributedCache(mux, Options.Create(new RedisCacheOptions { KeyPrefix = "test:" }));

        Assert.NotNull(cache);
    }

    [Fact]
    public void Constructor_Throws_OnNullMultiplexer()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new RedisBhenguDistributedCache(null!, Options.Create(new RedisCacheOptions())));
    }

    [Fact]
    public void Constructor_Throws_OnNullOptions()
    {
        var mux = new Mock<IConnectionMultiplexer>().Object;
        Assert.Throws<ArgumentNullException>(() => new RedisBhenguDistributedCache(mux, null!));
    }

    [Fact]
    public void AddBhenguRedisCache_ReplacesExistingInMemoryRegistration()
    {
        var services = new ServiceCollection()
            .AddBhenguInMemoryCache(); // pre-existing in-memory default

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bhengu:Finance:Payments:Redis:ConnectionString"] = "127.0.0.1:6379,abortConnect=false",
                ["Bhengu:Finance:Payments:Redis:KeyPrefix"] = "smoketest:",
            })
            .Build();

        services.AddBhenguRedisCache(config);

        // The in-memory descriptor should have been removed and replaced by the Redis one.
        var descriptor = services.Single(d => d.ServiceType == typeof(IBhenguDistributedCache));
        Assert.Equal(typeof(RedisBhenguDistributedCache), descriptor.ImplementationType);
    }

    [Fact(Skip = "Requires a running Redis instance — only meaningful for local integration testing.")]
    public async Task SetThenGet_RoundTripsValue_AgainstLiveRedis()
    {
        // To run this locally: docker run --rm -p 6379:6379 redis:7
        var mux = await ConnectionMultiplexer.ConnectAsync("localhost:6379,abortConnect=false");
        var cache = new RedisBhenguDistributedCache(mux, Options.Create(new RedisCacheOptions { KeyPrefix = "smoketest:" }));

        var key = Guid.NewGuid().ToString();
        await cache.SetAsync(key, new Payload { Hello = "Redis" }, TimeSpan.FromMinutes(1));
        var retrieved = await cache.GetAsync<Payload>(key);
        Assert.NotNull(retrieved);
        await cache.RemoveAsync(key);
    }

    private sealed class Payload
    {
        public string Hello { get; set; } = "";
    }
}
