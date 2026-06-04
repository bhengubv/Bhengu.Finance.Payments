// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Pesapal.Configuration;
using Bhengu.Finance.Payments.Pesapal.Internals;
using Bhengu.Finance.Payments.Pesapal.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Pesapal;

public class PesapalSettlementProviderTests
{
    private static PesapalSettlementProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new PesapalOptions { ConsumerKey = "ck", ConsumerSecret = "cs", Currency = "KES" }),
            NullLogger<PesapalSettlementProvider>.Instance,
            new PesapalTokenCache());

    private static StubHttpMessageHandler StubWithToken(Func<HttpRequestMessage, HttpResponseMessage> resourceHandler) =>
        new((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("Auth/RequestToken"))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"tok","expiryDate":"2026-06-04T00:00:00Z"}""");
            return resourceHandler(req);
        });

    [Fact]
    public void Constructor_Throws_WhenConsumerSecretMissing() =>
        Assert.Throws<ProviderConfigurationException>(() => new PesapalSettlementProvider(
            new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new PesapalOptions { ConsumerKey = "ck" }),
            NullLogger<PesapalSettlementProvider>.Instance));

    [Fact]
    public async Task ListSettlementsAsync_ReturnsMappedSettlements()
    {
        var provider = Create(StubWithToken(req =>
        {
            Assert.Contains("GetStatement?startDate=", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":[
                    {"settlement_id":"st-1","net_amount":990.5,"gross_amount":1000,"fees":9.5,"currency":"KES","settlement_date":"2026-06-01T00:00:00Z","transaction_count":3}
                ]}
                """);
        }));
        var settlements = await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow).ToListAsync();
        Assert.Single(settlements);
        Assert.Equal("st-1", settlements[0].Reference);
        Assert.Equal(990.5m, settlements[0].NetAmount);
    }

    [Fact]
    public async Task GetSettlementAsync_ReturnsNull_On404()
    {
        var provider = Create(StubWithToken(_ => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing")));
        Assert.Null(await provider.GetSettlementAsync("st-missing"));
    }

    [Fact]
    public async Task ListTransactionsAsync_MapsKinds()
    {
        var provider = Create(StubWithToken(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":[
                    {"confirmation_code":"cc-a","amount":100,"net_amount":99,"fees":1,"currency":"KES","transaction_type":"sale"},
                    {"confirmation_code":"cc-b","amount":50,"net_amount":50,"fees":0,"currency":"KES","transaction_type":"refund"}
                ]}
                """)));
        var txns = await provider.ListTransactionsAsync("st-1").ToListAsync();
        Assert.Equal(2, txns.Count);
        Assert.Equal(SettlementTransactionKind.Charge, txns[0].Kind);
        Assert.Equal(SettlementTransactionKind.Refund, txns[1].Kind);
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws429AsRateLimit()
    {
        var provider = Create(StubWithToken(_ => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "slow")));
        await Assert.ThrowsAsync<ProviderRateLimitException>(async () => await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow).ToListAsync());
    }
}
