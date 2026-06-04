// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Cellulant.Configuration;
using Bhengu.Finance.Payments.Cellulant.Internals;
using Bhengu.Finance.Payments.Cellulant.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Cellulant;

public class CellulantSettlementProviderTests
{
    private static StubHttpMessageHandler ComposeWithToken(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> businessHandler) =>
        new((req, ct) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("oauth/token", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"access_token":"tok_test","expires_in":3600}
                    """);
            return businessHandler(req, ct);
        });

    private static CellulantSettlementProvider Create(StubHttpMessageHandler handler)
    {
        var opts = new CellulantOptions
        {
            ServiceCode = "TGNTEST",
            ClientId = "client-1",
            ClientSecret = "secret-1",
            UseSandbox = true
        };
        var http = new HttpClient(handler);
        var optsInst = Options.Create(opts);
        var broker = new CellulantTokenBroker(optsInst, NullLogger<CellulantTokenBroker>.Instance);
        return new CellulantSettlementProvider(http, optsInst, NullLogger<CellulantSettlementProvider>.Instance, broker);
    }

    [Fact]
    public async Task ListSettlementsAsync_ReturnsBatches()
    {
        var handler = ComposeWithToken((req, _) =>
        {
            Assert.Contains("settlements/v1", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":[{"settlementReference":"SET-1","grossAmount":1000,"netAmount":985,"fees":15,"currency":"KES","settledAt":"2026-06-04T08:00:00Z","transactionCount":3}]}
                """);
        });
        var provider = Create(handler);
        var list = await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);
        Assert.Single(list);
        Assert.Equal("SET-1", list[0].Reference);
        Assert.Equal(985m, list[0].NetAmount);
        Assert.Equal(3, list[0].TransactionCount);
    }

    [Fact]
    public async Task GetSettlementAsync_ReturnsNull_On404()
    {
        var handler = ComposeWithToken((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.Null(await provider.GetSettlementAsync("missing"));
    }

    [Fact]
    public async Task GetSettlementAsync_ReturnsBatch_OnSuccess()
    {
        var handler = ComposeWithToken((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"settlement":{"settlementReference":"SET-1","netAmount":500,"currency":"KES","settledAt":"2026-06-04T08:00:00Z"}}
            """));
        var provider = Create(handler);
        var s = await provider.GetSettlementAsync("SET-1");
        Assert.NotNull(s);
        Assert.Equal("SET-1", s!.Reference);
        Assert.Equal(500m, s.NetAmount);
    }

    [Fact]
    public async Task ListTransactionsAsync_ReturnsConstituents()
    {
        var handler = ComposeWithToken((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"data":[{"transactionReference":"TX-1","kind":"charge","grossAmount":100,"netAmount":97,"fee":3,"currency":"KES","createdAt":"2026-06-04T07:30:00Z"}]}
            """));
        var provider = Create(handler);
        var list = await provider.ListTransactionsAsync("SET-1");
        Assert.Single(list);
        Assert.Equal(SettlementTransactionKind.Charge, list[0].Kind);
        Assert.Equal(97m, list[0].NetAmount);
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws429AsProviderRateLimitException()
    {
        var handler = ComposeWithToken((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() =>
            provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
    }

    [Fact]
    public async Task ListSettlementsAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = ComposeWithToken((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.InternalServerError, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
    }
}
