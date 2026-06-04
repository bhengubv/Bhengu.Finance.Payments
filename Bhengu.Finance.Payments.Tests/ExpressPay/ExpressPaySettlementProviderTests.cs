// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.ExpressPay.Configuration;
using Bhengu.Finance.Payments.ExpressPay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.ExpressPay;

public class ExpressPaySettlementProviderTests
{
    private static ExpressPaySettlementProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new ExpressPayOptions { MerchantId = "demo", ApiKey = "k", Currency = "GHS" }),
            NullLogger<ExpressPaySettlementProvider>.Instance);

    [Fact]
    public void Constructor_Throws_WhenApiKeyMissing() =>
        Assert.Throws<ProviderConfigurationException>(() => new ExpressPaySettlementProvider(
            new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new ExpressPayOptions { MerchantId = "m" }),
            NullLogger<ExpressPaySettlementProvider>.Instance));

    [Fact]
    public async Task ListSettlementsAsync_ReturnsMappedSettlements()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("settlements.php", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"settlements":[
                    {"settlement-id":"st-1","net-amount":990.5,"gross-amount":1000,"fees":9.5,"currency":"GHS","settled-at":"2026-06-01T00:00:00Z","transaction-count":3}
                ]}
                """);
        });
        var provider = Create(handler);
        var settlements = await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow).ToListAsync();
        Assert.Single(settlements);
        Assert.Equal("st-1", settlements[0].Reference);
        Assert.Equal(990.5m, settlements[0].NetAmount);
    }

    [Fact]
    public async Task GetSettlementAsync_ReturnsNull_On404()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.Null(await provider.GetSettlementAsync("st-missing"));
    }

    [Fact]
    public async Task ListTransactionsAsync_MapsKinds()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"transactions":[
                    {"token":"tok-a","amount":100,"net-amount":99,"fee":1,"currency":"GHS","type":"sale"},
                    {"token":"tok-b","amount":50,"net-amount":50,"fee":0,"currency":"GHS","type":"refund"}
                ]}
                """));
        var provider = Create(handler);
        var txns = await provider.ListTransactionsAsync("st-1").ToListAsync();
        Assert.Equal(2, txns.Count);
        Assert.Equal(SettlementTransactionKind.Charge, txns[0].Kind);
        Assert.Equal(SettlementTransactionKind.Refund, txns[1].Kind);
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "slow"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(async () => await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow).ToListAsync());
    }

    [Fact]
    public async Task ListSettlementsAsync_WrapsHttpRequestExceptionAsUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS fail"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(async () => await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow).ToListAsync());
    }
}
