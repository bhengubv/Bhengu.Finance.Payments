// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.CMI.Configuration;
using Bhengu.Finance.Payments.CMI.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.CMI;

public class CMISettlementProviderTests
{
    private static CMISettlementProvider Create(StubHttpMessageHandler handler)
    {
        var opts = new CMIOptions { ClientId = "600", StoreKey = "store", ApiUser = "u", ApiPassword = "p", Currency = "504" };
        var http = new HttpClient(handler);
        return new CMISettlementProvider(http, Options.Create(opts), NullLogger<CMISettlementProvider>.Instance);
    }

    [Fact]
    public async Task ListSettlementsAsync_MapsResults()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.OK,
            "<CC5Response><Settlement><Id>S1</Id><NetAmount>1000.00</NetAmount><GrossAmount>1100.00</GrossAmount><Fees>100.00</Fees><Currency>MAD</Currency><SettledAt>2026-06-01T00:00:00Z</SettledAt><Count>5</Count></Settlement></CC5Response>"));
        var provider = Create(handler);
        var list = await provider.ListSettlementsAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 30));
        Assert.Single(list);
        Assert.Equal("S1", list[0].Reference);
        Assert.Equal(1000m, list[0].NetAmount);
        Assert.Equal(100m, list[0].Fees);
        Assert.Equal(5, list[0].TransactionCount);
    }

    [Fact]
    public async Task GetSettlementAsync_Returns404AsNull()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "no"));
        var provider = Create(handler);
        Assert.Null(await provider.GetSettlementAsync("S1"));
    }

    [Fact]
    public async Task ListTransactionsAsync_MapsLineItems()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.OK,
            "<CC5Response>" +
            "<Transaction><OrderId>O1</OrderId><Type>Sale</Type><NetAmount>500.00</NetAmount><Currency>MAD</Currency><Date>2026-06-04T00:00:00Z</Date></Transaction>" +
            "<Transaction><OrderId>O2</OrderId><Type>Credit</Type><NetAmount>-100.00</NetAmount><Currency>MAD</Currency><Date>2026-06-04T00:00:00Z</Date></Transaction>" +
            "</CC5Response>"));
        var provider = Create(handler);
        var txns = await provider.ListTransactionsAsync("S1");
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
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
    }

    [Fact]
    public async Task ListSettlementsAsync_WrapsNetworkAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ListSettlementsAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
    }
}
