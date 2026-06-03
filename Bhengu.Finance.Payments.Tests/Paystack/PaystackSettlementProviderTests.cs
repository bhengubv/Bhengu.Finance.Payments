// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paystack;

public class PaystackSettlementProviderTests
{
    private static PaystackSettlementProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new PaystackOptions { SecretKey = "sk_test_xx" }),
            NullLogger<PaystackSettlementProvider>.Instance);

    [Fact]
    public void Constructor_Throws_WhenSecretKeyMissing() =>
        Assert.Throws<ProviderConfigurationException>(() => new PaystackSettlementProvider(
            new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new PaystackOptions()),
            NullLogger<PaystackSettlementProvider>.Instance));

    [Fact]
    public async Task ListSettlementsAsync_ReturnsMappedSettlements()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("settlement?perPage=100", req.RequestUri!.PathAndQuery);
            Assert.Contains("from=", req.RequestUri.PathAndQuery);
            Assert.Contains("to=", req.RequestUri.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"data":[
                    {"id":11,"total_amount":100000,"net_amount":99000,"total_transactions":5,"settlement_bank":"044","currency":"NGN","status":"success","createdAt":"2026-06-01T00:00:00Z","settled_at":"2026-06-02T00:00:00Z"},
                    {"id":12,"total_amount":250000,"net_amount":247500,"total_transactions":12,"settlement_bank":"058","currency":"NGN","status":"success","createdAt":"2026-06-03T00:00:00Z"}
                ]}
                """);
        });
        var provider = Create(handler);
        var settlements = await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow);
        Assert.Equal(2, settlements.Count);
        Assert.Equal("11", settlements[0].Reference);
        Assert.Equal(1000m, settlements[0].NetAmount);
        Assert.Equal(5, settlements[0].TransactionCount);
    }

    [Fact]
    public async Task GetSettlementAsync_ReturnsSettlement_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("settlement/STL_1", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"data":{"id":1,"total_amount":500000,"net_amount":495000,"total_transactions":20,"currency":"NGN","settled_at":"2026-06-01T00:00:00Z"}}
                """);
        });
        var provider = Create(handler);
        var settlement = await provider.GetSettlementAsync("STL_1");
        Assert.NotNull(settlement);
        Assert.Equal(5000m, settlement!.NetAmount);
        Assert.Equal(20, settlement.TransactionCount);
    }

    [Fact]
    public async Task GetSettlementAsync_ReturnsNull_On404()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.Null(await provider.GetSettlementAsync("STL_missing"));
    }

    [Fact]
    public async Task ListTransactionsAsync_ReturnsMappedTransactions()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("settlement/STL_1/transactions", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"data":[
                    {"reference":"ref_a","amount":10000,"fees":150,"currency":"NGN","status":"success","channel":"card","paid_at":"2026-06-01T00:00:00Z"},
                    {"reference":"ref_b","amount":-5000,"fees":0,"currency":"NGN","status":"reversed","channel":"card","paid_at":"2026-06-01T01:00:00Z"}
                ]}
                """);
        });
        var provider = Create(handler);
        var txs = await provider.ListTransactionsAsync("STL_1");
        Assert.Equal(2, txs.Count);
        Assert.Equal(SettlementTransactionKind.Charge, txs[0].Kind);
        Assert.Equal(SettlementTransactionKind.Refund, txs[1].Kind);
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "slow"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
    }

    [Fact]
    public async Task ListTransactionsAsync_ThrowsProviderUnavailable_OnNetworkError()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("connection refused"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ListTransactionsAsync("STL_x"));
    }
}
