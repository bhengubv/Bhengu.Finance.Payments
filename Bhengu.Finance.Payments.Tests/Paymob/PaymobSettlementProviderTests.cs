// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Paymob.Configuration;
using Bhengu.Finance.Payments.Paymob.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paymob;

public class PaymobSettlementProviderTests
{
    private static PaymobSettlementProvider Create(StubHttpMessageHandler handler)
    {
        var opts = new PaymobOptions { ApiKey = "k", IntegrationId = 1, Currency = "EGP" };
        var http = new HttpClient(handler);
        return new PaymobSettlementProvider(http, Options.Create(opts), NullLogger<PaymobSettlementProvider>.Instance);
    }

    [Fact]
    public async Task ListSettlementsAsync_MapsResults()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("auth/tokens", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"t"}""");
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"results":[{"id":11,"net_amount_cents":500000,"gross_amount_cents":510000,"fee_cents":10000,"currency":"EGP","settled_at":"2026-06-01T00:00:00Z","transaction_count":12}]}""");
        });
        var provider = Create(handler);
        var list = await provider.ListSettlementsAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 30)).ToListAsync();
        Assert.Single(list);
        Assert.Equal("11", list[0].Reference);
        Assert.Equal(5000m, list[0].NetAmount);
        Assert.Equal(100m, list[0].Fees);
        Assert.Equal(12, list[0].TransactionCount);
    }

    [Fact]
    public async Task GetSettlementAsync_Returns404AsNull()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("auth/tokens", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"t"}""");
            return StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "no");
        });
        var provider = Create(handler);
        Assert.Null(await provider.GetSettlementAsync("missing"));
    }

    [Fact]
    public async Task ListTransactionsAsync_MapsLineItems()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("auth/tokens", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"t"}""");
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"results":[{"transaction_id":1,"type":"charge","net_amount_cents":50000,"currency":"EGP","created_at":"2026-06-04T00:00:00Z"},{"transaction_id":2,"type":"refund","net_amount_cents":-10000,"currency":"EGP","created_at":"2026-06-04T00:00:00Z"}]}""");
        });
        var provider = Create(handler);
        var txns = await provider.ListTransactionsAsync("11").ToListAsync();
        Assert.Equal(2, txns.Count);
        Assert.Equal(SettlementTransactionKind.Charge, txns[0].Kind);
        Assert.Equal(SettlementTransactionKind.Refund, txns[1].Kind);
        Assert.Equal(-100m, txns[1].NetAmount);
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(async () => await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow).ToListAsync());
    }

    [Fact]
    public async Task ListSettlementsAsync_WrapsNetworkAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(async () => await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow).ToListAsync());
    }
}
