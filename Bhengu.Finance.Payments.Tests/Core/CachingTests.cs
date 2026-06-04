// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Caching;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.CoreFoundations;

/// <summary>
/// Exercises the foundational <see cref="InMemoryBhenguDistributedCache"/>. Set/get round-trips,
/// TTL expiry, removal, concurrent access, null-value rejection, and JSON-serialisation isolation
/// (mutating the original object post-Set must not change what Get returns).
/// </summary>
public class CachingTests
{
    private sealed class Sample
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    [Fact]
    public async Task SetThenGet_RoundTripsValue()
    {
        var cache = new InMemoryBhenguDistributedCache();
        var sample = new Sample { Name = "PayShap", Count = 42 };

        await cache.SetAsync("k1", sample, TimeSpan.FromMinutes(5));
        var roundTripped = await cache.GetAsync<Sample>("k1");

        Assert.NotNull(roundTripped);
        Assert.Equal("PayShap", roundTripped!.Name);
        Assert.Equal(42, roundTripped.Count);
    }

    [Fact]
    public async Task Get_ReturnsNull_WhenKeyAbsent()
    {
        var cache = new InMemoryBhenguDistributedCache();
        var result = await cache.GetAsync<Sample>("never-set");
        Assert.Null(result);
    }

    [Fact]
    public async Task Get_ReturnsNull_AfterTtlExpires()
    {
        var cache = new InMemoryBhenguDistributedCache();
        await cache.SetAsync("ephemeral", new Sample { Name = "x" }, TimeSpan.FromMilliseconds(50));

        await Task.Delay(120);

        var result = await cache.GetAsync<Sample>("ephemeral");
        Assert.Null(result);
    }

    [Fact]
    public async Task Remove_DeletesEntry()
    {
        var cache = new InMemoryBhenguDistributedCache();
        await cache.SetAsync("k", new Sample { Name = "x" }, TimeSpan.FromMinutes(5));
        await cache.RemoveAsync("k");

        Assert.Null(await cache.GetAsync<Sample>("k"));
    }

    [Fact]
    public async Task Remove_IsNoOp_WhenKeyAbsent()
    {
        var cache = new InMemoryBhenguDistributedCache();
        // Must not throw when removing a key that was never set.
        await cache.RemoveAsync("absent");
    }

    [Fact]
    public async Task Set_Throws_OnNullValue()
    {
        var cache = new InMemoryBhenguDistributedCache();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            cache.SetAsync<Sample>("k", null!, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Set_AcceptsConcurrentWritesAcrossKeys()
    {
        var cache = new InMemoryBhenguDistributedCache();
        var tasks = Enumerable.Range(0, 50).Select(i =>
            cache.SetAsync($"k-{i}", new Sample { Name = $"name-{i}", Count = i }, TimeSpan.FromMinutes(5)));
        await Task.WhenAll(tasks);

        for (var i = 0; i < 50; i++)
        {
            var v = await cache.GetAsync<Sample>($"k-{i}");
            Assert.NotNull(v);
            Assert.Equal(i, v!.Count);
        }
    }

    [Fact]
    public async Task SetThenMutateOriginal_DoesNotAffectStoredValue()
    {
        // The in-memory cache serialises on Set so mutating the original object after the call
        // must not leak through into the cached value (this is what makes the in-memory cache and
        // a real Redis-backed cache behave identically — no shared reference surprises).
        var cache = new InMemoryBhenguDistributedCache();
        var original = new Sample { Name = "initial", Count = 1 };
        await cache.SetAsync("k", original, TimeSpan.FromMinutes(5));

        original.Name = "mutated";
        original.Count = 999;

        var retrieved = await cache.GetAsync<Sample>("k");
        Assert.NotNull(retrieved);
        Assert.Equal("initial", retrieved!.Name);
        Assert.Equal(1, retrieved.Count);
    }
}
