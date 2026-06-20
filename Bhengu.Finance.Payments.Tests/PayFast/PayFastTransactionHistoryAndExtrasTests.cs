// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.PayFast.Builders;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Bhengu.Finance.Payments.PayFast.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayFast;

/// <summary>PayFast Transaction History API (range/daily/weekly/monthly) — verified against the official SDK.</summary>
public class PayFastTransactionHistoryTests
{
    private static (PayFastTransactionHistoryProvider Provider, List<(HttpMethod Method, string Uri)> Requests) Make(string csv = "ref,amount\nX,100")
    {
        var requests = new List<(HttpMethod, string)>();
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            requests.Add((req.Method, req.RequestUri!.ToString()));
            return StubHttpMessageHandler.Text(HttpStatusCode.OK, csv);
        });
        var provider = new PayFastTransactionHistoryProvider(
            new HttpClient(handler),
            Options.Create(new PayFastOptions { MerchantId = "10000100", Passphrase = "pp", UseSandbox = false }),
            NullLogger<PayFastTransactionHistoryProvider>.Instance);
        return (provider, requests);
    }

    [Fact]
    public async Task GetHistoryAsync_QueriesRange_ReturnsRawCsv()
    {
        var (provider, requests) = Make("a,b\n1,2");

        var csv = await provider.GetHistoryAsync(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 7), offset: 0, limit: 1000);

        Assert.Equal("a,b\n1,2", csv);
        var req = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Contains("transactions/history?", req.Uri);
        Assert.Contains("from=2026-06-01", req.Uri);
        Assert.Contains("to=2026-06-07", req.Uri);
        Assert.Contains("limit=1000", req.Uri);
    }

    [Fact]
    public async Task GetHistoryAsync_SwapsReversedDates()
    {
        var (provider, requests) = Make();
        await provider.GetHistoryAsync(new DateOnly(2026, 6, 7), new DateOnly(2026, 6, 1));
        var req = Assert.Single(requests);
        Assert.Contains("from=2026-06-01", req.Uri);
        Assert.Contains("to=2026-06-07", req.Uri);
    }

    [Fact]
    public async Task GetDailyHistoryAsync_UsesDate()
    {
        var (provider, requests) = Make();
        await provider.GetDailyHistoryAsync(new DateOnly(2026, 6, 7));
        var req = Assert.Single(requests);
        Assert.Contains("transactions/history/daily?", req.Uri);
        Assert.Contains("date=2026-06-07", req.Uri);
    }

    [Fact]
    public async Task GetMonthlyHistoryAsync_UsesYearMonth()
    {
        var (provider, requests) = Make();
        await provider.GetMonthlyHistoryAsync(2026, 6);
        var req = Assert.Single(requests);
        Assert.Contains("transactions/history/monthly?", req.Uri);
        Assert.Contains("date=2026-06", req.Uri);
    }
}

/// <summary>Card-update link builder + refund-fetch (both production-only).</summary>
public class PayFastCardUpdateAndRefundFetchTests
{
    private static PayFastFormBuilder FormBuilder(bool sandbox) =>
        new(Options.Create(new PayFastOptions { MerchantId = "10000100", MerchantKey = "k", Passphrase = "pp", UseSandbox = sandbox }),
            NullLogger<PayFastFormBuilder>.Instance);

    [Fact]
    public void BuildCardUpdateUrl_Production_BuildsRecurringUpdateLink()
    {
        var url = FormBuilder(sandbox: false).BuildCardUpdateUrl("TOKEN-1", "https://shop.example/return");
        Assert.StartsWith("https://www.payfast.co.za/eng/recurring/update/TOKEN-1", url);
        Assert.Contains("return=", url);
    }

    [Fact]
    public void BuildCardUpdateUrl_Sandbox_Throws() =>
        Assert.Throws<BhenguPaymentException>(() => FormBuilder(sandbox: true).BuildCardUpdateUrl("TOKEN-1"));

    [Fact]
    public async Task FetchRefundAsync_Production_GetsRefund_ReturnsRawBody()
    {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            requests.Add(req.RequestUri!.ToString());
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"code":200,"status":"complete"}""");
        });
        var provider = new PayFastPaymentProvider(new HttpClient(handler),
            Options.Create(new PayFastOptions { MerchantId = "10000100", Passphrase = "pp", UseSandbox = false }),
            NullLogger<PayFastPaymentProvider>.Instance);

        var body = await provider.FetchRefundAsync("PF-PAY-1");

        Assert.Contains("complete", body);
        Assert.Contains("refunds/PF-PAY-1", Assert.Single(requests));
    }

    [Fact]
    public async Task FetchRefundAsync_Sandbox_Throws()
    {
        var provider = new PayFastPaymentProvider(
            new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"))),
            Options.Create(new PayFastOptions { MerchantId = "10000100", Passphrase = "pp", UseSandbox = true }),
            NullLogger<PayFastPaymentProvider>.Instance);

        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.FetchRefundAsync("PF-PAY-1"));
    }
}
