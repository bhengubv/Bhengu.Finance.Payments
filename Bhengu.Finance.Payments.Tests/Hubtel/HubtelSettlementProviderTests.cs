// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Hubtel.Configuration;
using Bhengu.Finance.Payments.Hubtel.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Hubtel;

public class HubtelSettlementProviderTests
{
    private static HubtelSettlementProvider Create(StubHttpMessageHandler handler, HubtelOptions? opts = null)
    {
        opts ??= new HubtelOptions
        {
            ClientId = "ci",
            ClientSecret = "cs",
            MerchantAccountNumber = "POS-1",
            Currency = "GHS"
        };
        return new HubtelSettlementProvider(new HttpClient(handler), Options.Create(opts), NullLogger<HubtelSettlementProvider>.Instance);
    }

    [Fact]
    public void Constructor_Throws_WhenMerchantAccountMissing() =>
        Assert.Throws<ProviderConfigurationException>(() => new HubtelSettlementProvider(
            new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new HubtelOptions { ClientId = "c", ClientSecret = "s" }),
            NullLogger<HubtelSettlementProvider>.Instance));

    [Fact]
    public async Task ListSettlementsAsync_ReturnsMappedSettlements()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains("statement?from=", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":[
                    {"statementId":"st-1","netAmount":990.50,"grossAmount":1000,"charges":9.50,"currency":"GHS","settledAt":"2026-06-01T00:00:00Z","transactionCount":3},
                    {"statementId":"st-2","netAmount":2475,"grossAmount":2500,"charges":25,"currency":"GHS","settledAt":"2026-06-03T00:00:00Z","transactionCount":12}
                ]}
                """);
        });
        var provider = Create(handler);
        var settlements = await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow);
        Assert.Equal(2, settlements.Count);
        Assert.Equal("st-1", settlements[0].Reference);
        Assert.Equal(990.50m, settlements[0].NetAmount);
        Assert.Equal(3, settlements[0].TransactionCount);
    }

    [Fact]
    public async Task GetSettlementAsync_ReturnsNull_On404()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.Null(await provider.GetSettlementAsync("st-missing"));
    }

    [Fact]
    public async Task GetSettlementAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "slow"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.GetSettlementAsync("st-1"));
    }

    [Fact]
    public async Task ListTransactionsAsync_MapsKinds()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("transactions/", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":[
                    {"transactionId":"tx-a","amount":100,"amountAfterCharges":99,"charges":1,"currency":"GHS","transactionType":"sale","createdAt":"2026-06-01T00:00:00Z"},
                    {"transactionId":"tx-b","amount":50,"amountAfterCharges":50,"charges":0,"currency":"GHS","transactionType":"refund","createdAt":"2026-06-01T01:00:00Z"}
                ]}
                """);
        });
        var provider = Create(handler);
        var txns = await provider.ListTransactionsAsync("st-1");
        Assert.Equal(2, txns.Count);
        Assert.Equal(SettlementTransactionKind.Charge, txns[0].Kind);
        Assert.Equal(SettlementTransactionKind.Refund, txns[1].Kind);
    }

    [Fact]
    public async Task ListSettlementsAsync_WrapsHttpRequestExceptionAsUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS fail"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
    }
}
