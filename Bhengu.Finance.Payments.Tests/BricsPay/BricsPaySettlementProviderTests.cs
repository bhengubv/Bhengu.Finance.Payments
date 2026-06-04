// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.BricsPay.Configuration;
using Bhengu.Finance.Payments.BricsPay.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.BricsPay;

public class BricsPaySettlementProviderTests
{
    private static BricsPaySettlementProvider Create(StubHttpMessageHandler handler)
    {
        var opts = new BricsPayOptions
        {
            MerchantId = "BRICS_TEST",
            SecretKey = "secret",
            UseSandbox = true
        };
        var http = new HttpClient(handler);
        return new BricsPaySettlementProvider(http, Options.Create(opts), NullLogger<BricsPaySettlementProvider>.Instance);
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
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("/settlements", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"settlements":[{"settlement_reference":"S-1","gross_amount":1000,"net_amount":985,"fees":15,"currency":"ZAR","settled_at":"2026-06-04T08:00:00Z","transaction_count":3}]}
                """);
        });
        var provider = Create(handler);
        var list = await ToListAsync(provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow));
        Assert.Single(list);
        Assert.Equal("S-1", list[0].Reference);
        Assert.Equal(985m, list[0].NetAmount);
    }

    [Fact]
    public async Task GetSettlementAsync_ReturnsBatch_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"settlement":{"settlement_reference":"S-1","net_amount":500,"currency":"ZAR","settled_at":"2026-06-04T08:00:00Z"}}
            """));
        var provider = Create(handler);
        var s = await provider.GetSettlementAsync("S-1");
        Assert.NotNull(s);
        Assert.Equal(500m, s!.NetAmount);
    }

    [Fact]
    public async Task GetSettlementAsync_ReturnsNull_On404()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.Null(await provider.GetSettlementAsync("missing"));
    }

    [Fact]
    public async Task ListTransactionsAsync_ReturnsConstituents()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"transactions":[{"transaction_reference":"TX-1","kind":"charge","gross_amount":100,"net_amount":97,"fee":3,"currency":"ZAR","created_at":"2026-06-04T07:00:00Z"}]}
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
}
