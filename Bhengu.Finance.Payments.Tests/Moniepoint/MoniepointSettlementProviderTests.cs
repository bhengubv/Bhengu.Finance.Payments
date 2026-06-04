// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Moniepoint.Configuration;
using Bhengu.Finance.Payments.Moniepoint.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Moniepoint;

public class MoniepointSettlementProviderTests
{
    private static MoniepointSettlementProvider Create(StubHttpMessageHandler handler)
    {
        var opts = new MoniepointOptions { ApiKey = "k", MerchantId = "m" };
        var http = new HttpClient(handler);
        return new MoniepointSettlementProvider(http, Options.Create(opts), NullLogger<MoniepointSettlementProvider>.Instance);
    }

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source) list.Add(item);
        return list;
    }

    [Fact]
    public async Task ListSettlementsAsync_MapsResults()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("settlements", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"data":[
                    {"id":"S-1","reference":"S-1","netAmount":4950,"grossAmount":5000,"fees":50,"currency":"NGN","settledAt":"2026-06-01T00:00:00Z","transactionCount":3,"bankAccount":"0123456789"}
                ]}
                """);
        });
        var provider = Create(handler);
        var list = await Drain(provider.ListSettlementsAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 30)));
        Assert.Single(list);
        Assert.Equal("S-1", list[0].Reference);
        Assert.Equal(4950m, list[0].NetAmount);
        Assert.Equal(50m, list[0].Fees);
        Assert.Equal(3, list[0].TransactionCount);
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
                {"status":true,"data":[
                    {"reference":"TXN-1","type":"charge","netAmount":99,"grossAmount":100,"fee":1,"currency":"NGN","createdAt":"2026-06-04T00:00:00Z"},
                    {"reference":"TXN-2","type":"refund","netAmount":-50,"grossAmount":50,"fee":0,"currency":"NGN","createdAt":"2026-06-04T00:00:00Z"}
                ]}
                """));
        var provider = Create(handler);
        var txns = await Drain(provider.ListTransactionsAsync("S-1"));
        Assert.Equal(2, txns.Count);
        Assert.Equal(SettlementTransactionKind.Charge, txns[0].Kind);
        Assert.Equal(SettlementTransactionKind.Refund, txns[1].Kind);
        Assert.Equal(-50m, txns[1].NetAmount);
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() =>
            Drain(provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow)));
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws4xxAsPaymentDeclined()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            Drain(provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow)));
    }

    [Fact]
    public async Task ListSettlementsAsync_WrapsNetworkFailureAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            Drain(provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow)));
    }
}
