// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.OPay.Configuration;
using Bhengu.Finance.Payments.OPay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.OPay;

public class OPaySettlementProviderTests
{
    private static OPaySettlementProvider Create(StubHttpMessageHandler handler)
    {
        var opts = new OPayOptions
        {
            PublicKey = "pub",
            SecretKey = "sec",
            MerchantId = "MERCH",
            Country = "NG"
        };
        var http = new HttpClient(handler);
        return new OPaySettlementProvider(http, Options.Create(opts), NullLogger<OPaySettlementProvider>.Instance);
    }

    [Fact]
    public async Task ListSettlementsAsync_MapsResults()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("settlement/list", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code":"00000","data":{"settlements":[
                    {"settlementId":"SET-1","grossAmount":500000,"netAmount":495000,"currency":"NGN","settledAt":"2026-06-01T00:00:00Z","bankAccount":"0123456789","transactionCount":4}
                ]}}
                """);
        });
        var provider = Create(handler);
        var list = await provider.ListSettlementsAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 30));
        Assert.Single(list);
        Assert.Equal("SET-1", list[0].Reference);
        Assert.Equal(4950m, list[0].NetAmount);
        Assert.Equal(50m, list[0].Fees);
        Assert.Equal(4, list[0].TransactionCount);
    }

    [Fact]
    public async Task GetSettlementAsync_Returns404AsNull()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "no"));
        var provider = Create(handler);
        Assert.Null(await provider.GetSettlementAsync("missing"));
    }

    [Fact]
    public async Task ListTransactionsAsync_MapsLineItems()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code":"00000","data":{"transactions":[
                    {"orderNo":"O-1","type":"charge","grossAmount":100000,"netAmount":99000,"fee":1000,"currency":"NGN","createdAt":"2026-06-04T00:00:00Z"},
                    {"orderNo":"O-2","type":"refund","grossAmount":50000,"netAmount":-50000,"fee":0,"currency":"NGN","createdAt":"2026-06-04T00:00:00Z"}
                ]}}
                """));
        var provider = Create(handler);
        var txns = await provider.ListTransactionsAsync("SET-1");
        Assert.Equal(2, txns.Count);
        Assert.Equal(SettlementTransactionKind.Charge, txns[0].Kind);
        Assert.Equal(SettlementTransactionKind.Refund, txns[1].Kind);
        Assert.Equal(-500m, txns[1].NetAmount);
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() =>
            provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws4xxAsPaymentDeclined()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
    }

    [Fact]
    public async Task ListSettlementsAsync_WrapsNetworkFailureAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
    }
}
