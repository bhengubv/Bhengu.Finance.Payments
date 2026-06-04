// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Interswitch.Configuration;
using Bhengu.Finance.Payments.Interswitch.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Interswitch;

public class InterswitchSettlementProviderTests
{
    private const string TokenJson = """{"access_token":"isw-tok","token_type":"bearer","expires_in":3600}""";

    private static InterswitchSettlementProvider Create(StubHttpMessageHandler handler)
    {
        var opts = new InterswitchOptions
        {
            ClientId = "isw-id",
            ClientSecret = "isw-secret",
            MerchantCode = "MX1",
            ProductId = "1",
            WebhookSecret = "wh"
        };
        var http = new HttpClient(handler);
        return new InterswitchSettlementProvider(http, Options.Create(opts),
            NullLogger<InterswitchSettlementProvider>.Instance);
    }

    private static StubHttpMessageHandler TokenThen(Func<HttpRequestMessage, HttpResponseMessage> apiHandler) =>
        new((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth/token", StringComparison.OrdinalIgnoreCase))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, TokenJson);
            return apiHandler(req);
        });

    [Fact]
    public async Task ListSettlementsAsync_MapsResults()
    {
        var handler = TokenThen(req =>
        {
            Assert.Contains("api/v2/settlements", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"settlements":[
                    {"settlementId":"SET-1","grossAmount":500000,"netAmount":495000,"currency":"NGN","settlementDate":"2026-06-01T00:00:00Z","transactionCount":3,"settlementAccount":"0123456789"}
                ],"totalCount":1}
                """);
        });
        var provider = Create(handler);
        var list = await provider.ListSettlementsAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 30));
        Assert.Single(list);
        Assert.Equal("SET-1", list[0].Reference);
        Assert.Equal(4950m, list[0].NetAmount);
        Assert.Equal(50m, list[0].Fees);
        Assert.Equal(3, list[0].TransactionCount);
    }

    [Fact]
    public async Task GetSettlementAsync_Returns404AsNull()
    {
        var handler = TokenThen(_ => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "no"));
        var provider = Create(handler);
        Assert.Null(await provider.GetSettlementAsync("missing"));
    }

    [Fact]
    public async Task ListTransactionsAsync_MapsLineItems()
    {
        var handler = TokenThen(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"transactions":[
                {"transactionRef":"TXN-1","transactionType":"charge","grossAmount":100000,"netAmount":99000,"fee":1000,"currency":"NGN","transactionDate":"2026-06-04T00:00:00Z"},
                {"transactionRef":"TXN-2","transactionType":"refund","grossAmount":50000,"netAmount":-50000,"fee":0,"currency":"NGN","transactionDate":"2026-06-04T00:00:00Z"}
            ]}
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
        var handler = TokenThen(_ => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() =>
            provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws4xxAsPaymentDeclined()
    {
        var handler = TokenThen(_ => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad params"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
    }

    [Fact]
    public async Task ListSettlementsAsync_WrapsNetworkFailureAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            req.RequestUri!.AbsolutePath.Contains("oauth/token", StringComparison.OrdinalIgnoreCase)
                ? StubHttpMessageHandler.Json(HttpStatusCode.OK, TokenJson)
                : throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
    }
}
