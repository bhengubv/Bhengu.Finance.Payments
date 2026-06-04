// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Remita.Configuration;
using Bhengu.Finance.Payments.Remita.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Remita;

public class RemitaSettlementProviderTests
{
    private static RemitaSettlementProvider Create(StubHttpMessageHandler handler)
    {
        var opts = new RemitaOptions { MerchantId = "M", ServiceTypeId = "S", ApiKey = "K" };
        var http = new HttpClient(handler);
        return new RemitaSettlementProvider(http, Options.Create(opts), NullLogger<RemitaSettlementProvider>.Instance);
    }

    [Fact]
    public async Task ListSettlementsAsync_MapsResults()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("settlement/list", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"settlements":[
                    {"settlementId":"SET-1","grossAmount":5000,"netAmount":4950,"fees":50,"currency":"NGN","settlementDate":"2026-06-01T00:00:00Z","transactionCount":4,"settlementAccount":"0123456789"}
                ]}
                """);
        });
        var provider = Create(handler);
        var list = await provider.ListSettlementsAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 30)).ToListAsync();
        Assert.Single(list);
        Assert.Equal("SET-1", list[0].Reference);
        Assert.Equal(4950m, list[0].NetAmount);
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
                {"transactions":[
                    {"rrr":"RRR-1","type":"charge","grossAmount":100,"netAmount":99,"fee":1,"currency":"NGN","transactionDate":"2026-06-04T00:00:00Z"},
                    {"rrr":"RRR-2","type":"refund","grossAmount":50,"netAmount":-50,"fee":0,"currency":"NGN","transactionDate":"2026-06-04T00:00:00Z"}
                ]}
                """));
        var provider = Create(handler);
        var txns = await provider.ListTransactionsAsync("SET-1").ToListAsync();
        Assert.Equal(2, txns.Count);
        Assert.Equal(SettlementTransactionKind.Charge, txns[0].Kind);
        Assert.Equal(SettlementTransactionKind.Refund, txns[1].Kind);
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(async () =>
            await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow).ToListAsync());
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws4xxAsPaymentDeclined()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(async () =>
            await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow).ToListAsync());
    }

    [Fact]
    public async Task ListSettlementsAsync_WrapsNetworkFailureAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(async () =>
            await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow).ToListAsync());
    }
}
