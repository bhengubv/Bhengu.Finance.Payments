// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Fawry.Configuration;
using Bhengu.Finance.Payments.Fawry.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Fawry;

public class FawrySettlementProviderTests
{
    private static FawrySettlementProvider Create(StubHttpMessageHandler handler)
    {
        var opts = new FawryOptions { MerchantCode = "MC", SecurityKey = "SK" };
        var http = new HttpClient(handler);
        return new FawrySettlementProvider(http, Options.Create(opts), NullLogger<FawrySettlementProvider>.Instance);
    }

    [Fact]
    public async Task ListSettlementsAsync_MapsResults()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("reports/settlements", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"settlements":[
                    {"settlementId":"S-1","grossAmount":1000.00,"netAmount":990.00,"fees":10.00,"currencyCode":"EGP","settlementDate":"2026-06-01T00:00:00Z","transactionCount":5,"bankAccountReference":"NBE-001"}
                ]}
                """);
        });
        var provider = Create(handler);
        var list = await provider.ListSettlementsAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 30));
        Assert.Single(list);
        Assert.Equal("S-1", list[0].Reference);
        Assert.Equal(990m, list[0].NetAmount);
        Assert.Equal(10m, list[0].Fees);
        Assert.Equal(5, list[0].TransactionCount);
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
                {"transactions":[
                    {"fawryRefNumber":"F-1","type":"charge","grossAmount":100,"netAmount":99,"fee":1,"currencyCode":"EGP","transactionDate":"2026-06-04T00:00:00Z"},
                    {"fawryRefNumber":"F-2","type":"refund","grossAmount":50,"netAmount":-50,"fee":0,"currencyCode":"EGP","transactionDate":"2026-06-04T00:00:00Z"}
                ]}
                """));
        var provider = Create(handler);
        var txns = await provider.ListTransactionsAsync("S-1");
        Assert.Equal(2, txns.Count);
        Assert.Equal(SettlementTransactionKind.Charge, txns[0].Kind);
        Assert.Equal(SettlementTransactionKind.Refund, txns[1].Kind);
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() =>
            provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws4xxAsPaymentDeclined()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad"));
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
