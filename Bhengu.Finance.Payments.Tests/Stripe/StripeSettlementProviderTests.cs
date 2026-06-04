// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Stripe;

[Collection(StripeConfigurationCollection.Name)]
public class StripeSettlementProviderTests
{
    private static StripeSettlementProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new StripeOptions { SecretKey = "sk_test_fake", WebhookSecret = "whsec_test_fake" }),
            NullLogger<StripeSettlementProvider>.Instance);

    [Fact]
    public async Task ListSettlementsAsync_ReturnsMappedPayouts()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("payouts", req.RequestUri!.PathAndQuery, StringComparison.Ordinal);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"object":"list","data":[
                    {"id":"po_1","object":"payout","amount":250000,"currency":"usd","arrival_date":1700000000,"destination":"ba_1","status":"paid"},
                    {"id":"po_2","object":"payout","amount":120000,"currency":"usd","arrival_date":1700100000,"destination":"ba_2","status":"paid"}
                ],"has_more":false}
                """);
        });
        var provider = Create(handler);
        var settlements = await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow).ToListAsync();

        Assert.Equal(2, settlements.Count);
        Assert.Equal("po_1", settlements[0].Reference);
        Assert.Equal(2500m, settlements[0].NetAmount);
        Assert.Equal("USD", settlements[0].Currency);
    }

    [Fact]
    public async Task GetSettlementAsync_ReturnsMappedSettlement()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"id":"po_3","object":"payout","amount":99999,"currency":"usd","arrival_date":1700200000,"destination":"ba_3","status":"paid"}
            """));
        var provider = Create(handler);
        var s = await provider.GetSettlementAsync("po_3");
        Assert.NotNull(s);
        Assert.Equal(999.99m, s!.NetAmount);
    }

    [Fact]
    public async Task GetSettlementAsync_ReturnsNull_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.NotFound, """
            {"error":{"type":"invalid_request_error","message":"No such payout"}}
            """));
        var provider = Create(handler);
        Assert.Null(await provider.GetSettlementAsync("po_missing"));
    }

    [Fact]
    public async Task ListTransactionsAsync_ReturnsMappedBalanceTransactions()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("balance_transactions", req.RequestUri!.PathAndQuery, StringComparison.Ordinal);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"object":"list","data":[
                    {"id":"txn_1","object":"balance_transaction","amount":10000,"net":9700,"fee":300,"currency":"usd","created":1700000000,"type":"charge","source":"ch_1","status":"available"},
                    {"id":"txn_2","object":"balance_transaction","amount":-5000,"net":-5000,"fee":0,"currency":"usd","created":1700050000,"type":"refund","source":"re_1","status":"available"}
                ],"has_more":false}
                """);
        });
        var provider = Create(handler);
        var transactions = await provider.ListTransactionsAsync("po_1").ToListAsync();

        Assert.Equal(2, transactions.Count);
        Assert.Equal(SettlementTransactionKind.Charge, transactions[0].Kind);
        Assert.Equal(97m, transactions[0].NetAmount);
        Assert.Equal(SettlementTransactionKind.Refund, transactions[1].Kind);
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.TooManyRequests, """
            {"error":{"type":"invalid_request_error","message":"Too many requests"}}
            """));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(async () =>
            await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow).ToListAsync());
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws5xxAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.BadGateway, """
            {"error":{"type":"api_error","message":"Stripe is down"}}
            """));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(async () =>
            await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow).ToListAsync());
    }
}
