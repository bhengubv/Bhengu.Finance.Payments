// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.PayUIndia.Configuration;
using Bhengu.Finance.Payments.PayUIndia.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayUIndia;

public class PayUIndiaSettlementProviderTests
{
    private static PayUIndiaSettlementProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new PayUIndiaOptions
            {
                MerchantKey = "gtKFFx",
                Salt = "eCwWELxi"
            }),
            NullLogger<PayUIndiaSettlementProvider>.Instance);

    [Fact]
    public async Task ListSettlementsAsync_DeserialisesArray()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("merchant/postservice.php", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"1","settlements":[
                  {"settlement_id":"S1","net_amount":1000.50,"gross_amount":1050.50,"fees":50,"currency":"INR","settled_at":"2026-05-01T00:00:00Z","transaction_count":10},
                  {"settlement_id":"S2","net_amount":2000,"gross_amount":2100,"fees":100,"currency":"INR","settled_at":"2026-05-02T00:00:00Z","transaction_count":20}
                ]}
                """);
        });
        var provider = Create(handler);
        var list = await provider.ListSettlementsAsync(
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc)).ToListAsync();

        Assert.Equal(2, list.Count);
        Assert.Equal("S1", list[0].Reference);
        Assert.Equal(1000.50m, list[0].NetAmount);
        Assert.Equal(10, list[0].TransactionCount);
    }

    [Fact]
    public async Task ListSettlementsAsync_EmptyCollection_ReturnsEmpty()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"1","settlements":[]}"""));
        var provider = Create(handler);
        var list = await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow).ToListAsync();
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetSettlementAsync_DeserialisesSingle()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"settlement_id":"S1","net_amount":1000,"gross_amount":1050,"fees":50,"currency":"INR","settled_at":"2026-05-01T00:00:00Z","transaction_count":10}
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
                {"status":"1","transactions":[
                  {"mihpayid":"403993715","txnid":"txn1","type":"payment","net_amount":99,"gross_amount":100,"fee":1,"currency":"INR","created_at":"2026-05-01T00:00:00Z"},
                  {"mihpayid":"403993716","txnid":"txn2","type":"refund","net_amount":-50,"gross_amount":-50,"fee":0,"currency":"INR","created_at":"2026-05-01T01:00:00Z"}
                ]}
                """));
        var provider = Create(handler);
        var list = await provider.ListTransactionsAsync("S1").ToListAsync();
        Assert.Equal(2, list.Count);
        Assert.Equal(SettlementTransactionKind.Charge, list[0].Kind);
        Assert.Equal(SettlementTransactionKind.Refund, list[1].Kind);
    }

    [Fact]
    public async Task ListSettlementsAsync_OnNetworkFailure_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(async () =>
            await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow).ToListAsync());
    }
}
