// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.Yoco.Configuration;
using Bhengu.Finance.Payments.Yoco.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Yoco;

public class YocoSettlementProviderTests
{
    private static YocoSettlementProvider Create(StubHttpMessageHandler handler)
    {
        var opts = new YocoOptions { SecretKey = "sk_test_xx", WebhookSecret = "ws" };
        var http = new HttpClient(handler);
        return new YocoSettlementProvider(http, Options.Create(opts), NullLogger<YocoSettlementProvider>.Instance);
    }

    [Fact]
    public async Task ListSettlementsAsync_ReturnsMappedBatches()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains("payouts", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":[
                    {"id":"po_1","amountInCents":150000,"netAmountInCents":145000,"feeInCents":5000,"currency":"ZAR","status":"completed","transactionCount":5,"createdAt":"2026-06-01T00:00:00Z","paidAt":"2026-06-02T00:00:00Z","bankAccountId":"ba_x"},
                    {"id":"po_2","amountInCents":80000,"netAmountInCents":78000,"feeInCents":2000,"currency":"ZAR","status":"completed","transactionCount":3,"createdAt":"2026-06-03T00:00:00Z"}
                ]}
                """);
        });
        var provider = Create(handler);

        var list = await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow).ToListAsync();
        Assert.Equal(2, list.Count);
        Assert.Equal("po_1", list[0].Reference);
        Assert.Equal(1450m, list[0].NetAmount);
        Assert.Equal(1500m, list[0].GrossAmount);
        Assert.Equal(50m, list[0].Fees);
        Assert.Equal("ZAR", list[0].Currency);
        Assert.Equal(5, list[0].TransactionCount);
        Assert.Equal("ba_x", list[0].BankAccountReference);
    }

    [Fact]
    public async Task GetSettlementAsync_ReturnsBatch_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("payouts/po_99", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"po_99","amountInCents":50000,"netAmountInCents":49000,"feeInCents":1000,"currency":"ZAR","transactionCount":2,"paidAt":"2026-06-04T00:00:00Z"}
                """);
        });
        var provider = Create(handler);

        var settlement = await provider.GetSettlementAsync("po_99");
        Assert.NotNull(settlement);
        Assert.Equal("po_99", settlement!.Reference);
        Assert.Equal(490m, settlement.NetAmount);
    }

    [Fact]
    public async Task GetSettlementAsync_ReturnsNull_On404()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "not found")));
        var result = await provider.GetSettlementAsync("po_missing");
        Assert.Null(result);
    }

    [Fact]
    public async Task ListTransactionsAsync_MapsKinds()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"data":[
                {"id":"t1","sourceId":"ch_1","amountInCents":10000,"netAmountInCents":9700,"feeInCents":300,"currency":"ZAR","type":"charge","createdAt":"2026-06-01T00:00:00Z"},
                {"id":"t2","sourceId":"rf_1","amountInCents":-5000,"netAmountInCents":-5000,"currency":"ZAR","type":"refund","createdAt":"2026-06-02T00:00:00Z"},
                {"id":"t3","amountInCents":-100,"feeInCents":-100,"currency":"ZAR","type":"fee","createdAt":"2026-06-03T00:00:00Z"}
            ]}
            """));
        var provider = Create(handler);

        var txs = await provider.ListTransactionsAsync("po_1").ToListAsync();
        Assert.Equal(3, txs.Count);
        Assert.Equal(SettlementTransactionKind.Charge, txs[0].Kind);
        Assert.Equal(SettlementTransactionKind.Refund, txs[1].Kind);
        Assert.Equal(SettlementTransactionKind.Fee, txs[2].Kind);
        Assert.Equal("ch_1", txs[0].GatewayReference);
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws5xxAsProviderUnavailable()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down")));
        await Assert.ThrowsAsync<ProviderUnavailableException>(async () =>
            await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow).ToListAsync());
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws429AsRateLimit()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "slow")));
        await Assert.ThrowsAsync<ProviderRateLimitException>(async () =>
            await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow).ToListAsync());
    }
}
