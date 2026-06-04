// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.DPO.Configuration;
using Bhengu.Finance.Payments.DPO.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.DPO;

public class DPOSettlementProviderTests
{
    private static DPOSettlementProvider Create(StubHttpMessageHandler handler)
    {
        var opts = new DPOOptions { CompanyToken = "DPO_TEST", UseSandbox = true };
        var http = new HttpClient(handler);
        return new DPOSettlementProvider(http, Options.Create(opts), NullLogger<DPOSettlementProvider>.Instance);
    }

    private static async Task<List<Settlement>> ToListAsync(IAsyncEnumerable<Settlement> source)
    {
        var list = new List<Settlement>();
        await foreach (var s in source) list.Add(s);
        return list;
    }

    private static async Task<List<SettlementTransaction>> ToListAsync(IAsyncEnumerable<SettlementTransaction> source)
    {
        var list = new List<SettlementTransaction>();
        await foreach (var t in source) list.Add(t);
        return list;
    }

    [Fact]
    public async Task ListSettlementsAsync_ReturnsBatches()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"Result":"000","Settlements":[{"SettlementReference":"S-1","GrossAmount":"1000.00","NetAmount":"970.00","Fees":"30.00","Currency":"USD","SettlementDate":"2026-06-04","TransactionCount":4}]}
            """));
        var provider = Create(handler);
        var list = await ToListAsync(provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow));
        Assert.Single(list);
        Assert.Equal("S-1", list[0].Reference);
        Assert.Equal(970m, list[0].NetAmount);
        Assert.Equal(4, list[0].TransactionCount);
    }

    [Fact]
    public async Task GetSettlementAsync_ReturnsBatch_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"Result":"000","Settlement":{"SettlementReference":"S-1","NetAmount":"500","Currency":"USD","SettlementDate":"2026-06-04"}}
            """));
        var provider = Create(handler);
        var s = await provider.GetSettlementAsync("S-1");
        Assert.NotNull(s);
        Assert.Equal(500m, s!.NetAmount);
    }

    [Fact]
    public async Task ListTransactionsAsync_ReturnsConstituents()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"Result":"000","Transactions":[{"TransactionToken":"TT-1","Kind":"charge","GrossAmount":"100","NetAmount":"97","Fee":"3","Currency":"USD","TransactionDate":"2026-06-04"}]}
            """));
        var provider = Create(handler);
        var list = await ToListAsync(provider.ListTransactionsAsync("S-1"));
        Assert.Single(list);
        Assert.Equal(SettlementTransactionKind.Charge, list[0].Kind);
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() =>
            ToListAsync(provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow)));
    }

    [Fact]
    public async Task ListSettlementsAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("net"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            ToListAsync(provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow)));
    }

    [Fact]
    public async Task ListSettlementsAsync_ReturnsEmpty_WhenNoData()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"Result":"000"}
            """));
        var provider = Create(handler);
        Assert.Empty(await ToListAsync(provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow)));
    }
}
