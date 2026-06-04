// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Paytm.Configuration;
using Bhengu.Finance.Payments.Paytm.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paytm;

public class PaytmSettlementProviderTests
{
    private static PaytmSettlementProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new PaytmOptions { MerchantId = "MID1", MerchantKey = "secret_key" }),
            NullLogger<PaytmSettlementProvider>.Instance);

    [Fact]
    public async Task ListSettlementsAsync_DeserialisesArray()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("settlement/info", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"body":{"settlements":[
                  {"settlementId":"S1","netAmount":1000.50,"grossAmount":1050.50,"fees":50,"currency":"INR","settledAt":"2026-05-01T00:00:00Z","transactionCount":10},
                  {"settlementId":"S2","netAmount":2000,"grossAmount":2100,"fees":100,"currency":"INR","settledAt":"2026-05-02T00:00:00Z","transactionCount":20}
                ]}}
                """);
        });
        var provider = Create(handler);
        var list = await provider.ListSettlementsAsync(
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2, list.Count);
        Assert.Equal("S1", list[0].Reference);
        Assert.Equal(1000.50m, list[0].NetAmount);
    }

    [Fact]
    public async Task ListSettlementsAsync_EmptyCollection_ReturnsEmpty()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"body":{"settlements":[]}}"""));
        var provider = Create(handler);
        var list = await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetSettlementAsync_DeserialisesSingle()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"body":{"settlementId":"S1","netAmount":1000,"grossAmount":1050,"fees":50,"currency":"INR","settledAt":"2026-05-01T00:00:00Z","transactionCount":10}}
                """));
        var provider = Create(handler);
        var settlement = await provider.GetSettlementAsync("S1");
        Assert.NotNull(settlement);
        Assert.Equal("S1", settlement!.Reference);
        Assert.Equal(1000m, settlement.NetAmount);
    }

    [Fact]
    public async Task GetSettlementAsync_ReturnsNull_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.Null(await provider.GetSettlementAsync("missing"));
    }

    [Fact]
    public async Task ListTransactionsAsync_DeserialisesItems()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"body":{"transactions":[
                  {"orderId":"O1","txnId":"T1","type":"payment","netAmount":99,"grossAmount":100,"fee":1,"currency":"INR","createdAt":"2026-05-01T00:00:00Z"},
                  {"orderId":"O2","txnId":"T2","type":"refund","netAmount":-50,"grossAmount":-50,"fee":0,"currency":"INR","createdAt":"2026-05-01T01:00:00Z"}
                ]}}
                """));
        var provider = Create(handler);
        var list = await provider.ListTransactionsAsync("S1");
        Assert.Equal(2, list.Count);
        Assert.Equal(SettlementTransactionKind.Charge, list[0].Kind);
        Assert.Equal(SettlementTransactionKind.Refund, list[1].Kind);
    }

    [Fact]
    public async Task ListSettlementsAsync_OnNetworkFailure_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
    }
}
