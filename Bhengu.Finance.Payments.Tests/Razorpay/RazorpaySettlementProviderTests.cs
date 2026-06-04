// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Bhengu.Finance.Payments.Razorpay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Razorpay;

public class RazorpaySettlementProviderTests
{
    private static RazorpaySettlementProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new RazorpayOptions { KeyId = "rzp_test_x", KeySecret = "secret_x" }),
            NullLogger<RazorpaySettlementProvider>.Instance);

    [Fact]
    public async Task ListSettlementsAsync_DeserialisesCollection()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains("v1/settlements", req.RequestUri!.PathAndQuery);
            Assert.Contains("from=", req.RequestUri!.PathAndQuery);
            Assert.Contains("to=", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"entity":"collection","count":2,"items":[
                  {"id":"setl_1","entity":"settlement","amount":100000,"fees":1180,"tax":212,"utr":"UTR1","created_at":1700000000},
                  {"id":"setl_2","entity":"settlement","amount":50000,"fees":590,"tax":106,"utr":"UTR2","created_at":1700003600}
                ]}
                """);
        });
        var provider = Create(handler);
        var settlements = await provider.ListSettlementsAsync(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc)).ToListAsync();

        Assert.Equal(2, settlements.Count);
        Assert.Equal("setl_1", settlements[0].Reference);
        Assert.Equal(1000m, settlements[0].NetAmount);
        Assert.Equal("UTR1", settlements[0].BankAccountReference);
    }

    [Fact]
    public async Task GetSettlementAsync_DeserialisesSingle()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v1/settlements/setl_1", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"setl_1","entity":"settlement","amount":100000,"fees":1180,"tax":212,"utr":"UTR1","created_at":1700000000}""");
        });
        var provider = Create(handler);
        var settlement = await provider.GetSettlementAsync("setl_1");

        Assert.NotNull(settlement);
        Assert.Equal("setl_1", settlement!.Reference);
        Assert.Equal(1000m, settlement.NetAmount);
        // fees + tax both go into the Fees aggregate; we exposed both as Fees on the model.
        Assert.NotNull(settlement.Fees);
    }

    [Fact]
    public async Task GetSettlementAsync_ReturnsNull_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.Null(await provider.GetSettlementAsync("setl_missing"));
    }

    [Fact]
    public async Task ListTransactionsAsync_HitsReconEndpoint()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v1/settlements/recon/combined", req.RequestUri!.PathAndQuery);
            Assert.Contains("settlement_id=setl_1", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"entity":"collection","count":2,"items":[
                  {"entity_id":"pay_1","type":"payment","credit":10000,"debit":0,"fee":236,"currency":"INR","created_at":1700000000,"settlement_id":"setl_1"},
                  {"entity_id":"rfnd_1","type":"refund","credit":0,"debit":5000,"fee":0,"currency":"INR","created_at":1700001000,"settlement_id":"setl_1"}
                ]}
                """);
        });
        var provider = Create(handler);
        var txns = await provider.ListTransactionsAsync("setl_1").ToListAsync();

        Assert.Equal(2, txns.Count);
        Assert.Equal(SettlementTransactionKind.Charge, txns[0].Kind);
        Assert.Equal(100m, txns[0].NetAmount);
        Assert.Equal(SettlementTransactionKind.Refund, txns[1].Kind);
        Assert.Equal(-50m, txns[1].NetAmount);
    }

    [Fact]
    public async Task ListSettlementsAsync_OnRateLimit_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "throttled"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(async () =>
            await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow).ToListAsync());
    }

    [Fact]
    public async Task ListSettlementsAsync_OnNetworkFailure_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("connect"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(async () =>
            await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow).ToListAsync());
    }
}
