// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Bhengu.Finance.Payments.Flutterwave.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Flutterwave;

public class FlutterwaveSettlementProviderTests
{
    private static FlutterwaveSettlementProvider Create(StubHttpMessageHandler handler, FlutterwaveOptions? opts = null)
    {
        opts ??= new FlutterwaveOptions { SecretKey = "FLWSECK_TEST-xxx" };
        var http = new HttpClient(handler);
        return new FlutterwaveSettlementProvider(http, Options.Create(opts), NullLogger<FlutterwaveSettlementProvider>.Instance);
    }

    [Fact]
    public async Task ListSettlementsAsync_ReturnsSettlements_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v3/settlements", req.RequestUri!.PathAndQuery);
            Assert.Contains("from=2026-01-01", req.RequestUri.Query);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","data":[
                    {"id":1001,"amount_settled":9500.00,"gross_amount":10000.00,"fee":500.00,"currency":"NGN","settled_at":"2026-01-05T08:00:00Z","transaction_count":5,"account_number":"0690000040"},
                    {"id":1002,"amount_settled":4800.00,"gross_amount":5000.00,"fee":200.00,"currency":"NGN","settled_at":"2026-01-06T08:00:00Z","transaction_count":2,"account_number":"0690000040"}
                ]}
                """);
        });
        var provider = Create(handler);

        var settlements = await provider.ListSettlementsAsync(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc)).ToListAsync();

        Assert.Equal(2, settlements.Count);
        Assert.Equal("1001", settlements[0].Reference);
        Assert.Equal(9500.00m, settlements[0].NetAmount);
        Assert.Equal(500.00m, settlements[0].Fees);
    }

    [Fact]
    public async Task ListSettlementsAsync_ReturnsEmpty_WhenNoData()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"success","data":[]}"""));
        var provider = Create(handler);
        var settlements = await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow).ToListAsync();
        Assert.Empty(settlements);
    }

    [Fact]
    public async Task GetSettlementAsync_ReturnsSettlement_WhenFound()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v3/settlements/1001", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","data":{"id":1001,"amount_settled":9500.00,"gross_amount":10000.00,"fee":500.00,"currency":"NGN","settled_at":"2026-01-05T08:00:00Z","transaction_count":5}}
                """);
        });
        var provider = Create(handler);

        var settlement = await provider.GetSettlementAsync("1001");

        Assert.NotNull(settlement);
        Assert.Equal("1001", settlement!.Reference);
        Assert.Equal(5, settlement.TransactionCount);
    }

    [Fact]
    public async Task GetSettlementAsync_ReturnsNull_WhenNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "absent"));
        var provider = Create(handler);
        Assert.Null(await provider.GetSettlementAsync("missing"));
    }

    [Fact]
    public async Task ListTransactionsAsync_ReturnsTransactions_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("settlement_id=1001", req.RequestUri!.Query);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","data":[
                    {"id":555,"tx_ref":"tx_a","amount":2000,"amount_settled":1950,"app_fee":50,"currency":"NGN","type":"charge","created_at":"2026-01-04T10:00:00Z"},
                    {"id":556,"tx_ref":"tx_b","amount":3000,"amount_settled":2925,"app_fee":75,"currency":"NGN","type":"refund","created_at":"2026-01-04T11:00:00Z"}
                ]}
                """);
        });
        var provider = Create(handler);

        var txns = await provider.ListTransactionsAsync("1001").ToListAsync();
        Assert.Equal(2, txns.Count);
        Assert.Equal(SettlementTransactionKind.Charge, txns[0].Kind);
        Assert.Equal(SettlementTransactionKind.Refund, txns[1].Kind);
    }

    [Fact]
    public async Task ListSettlementsAsync_Wraps5xxAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(async () =>
            await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow).ToListAsync());
    }

    [Fact]
    public async Task ListSettlementsAsync_WrapsHttpRequestExceptionAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS fail"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(async () =>
            await provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow).ToListAsync());
    }
}
