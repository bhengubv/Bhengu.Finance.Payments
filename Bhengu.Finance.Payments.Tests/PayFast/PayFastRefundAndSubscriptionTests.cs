// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Bhengu.Finance.Payments.PayFast.Internals;
using Bhengu.Finance.Payments.PayFast.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayFast;

/// <summary>
/// Tests the real PayFast refund API + subscription update / pause-cycles / frequency mapping,
/// verified against PayFast's official PHP SDK (see PAYFAST_API_REFERENCE.md).
/// </summary>
public class PayFastRefundTests
{
    private static (PayFastPaymentProvider Provider, List<(HttpMethod Method, string Uri, string Body)> Requests)
        MakeProvider(bool sandbox)
    {
        var requests = new List<(HttpMethod, string, string)>();
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            requests.Add((req.Method, req.RequestUri!.ToString(), body));
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"code":200,"status":"success"}""");
        });
        var provider = new PayFastPaymentProvider(
            new HttpClient(handler),
            Options.Create(new PayFastOptions { MerchantId = "10000100", Passphrase = "pp", UseSandbox = sandbox }),
            NullLogger<PayFastPaymentProvider>.Instance);
        return (provider, requests);
    }

    [Fact]
    public async Task ProcessRefundAsync_Production_PostsRefund_InCents_ReturnsPending()
    {
        var (provider, requests) = MakeProvider(sandbox: false);

        var result = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "PF-PAY-1",
            Amount = 100m,
            Reason = "Product returned"
        });

        Assert.Equal(PaymentStatus.Pending, result.Status);
        Assert.Equal("PF-PAY-1", result.GatewayReference);

        var req = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Contains("refunds/PF-PAY-1", req.Uri);
        Assert.Contains("amount=10000", req.Body);   // R100.00 -> 10000 cents
        Assert.Contains("notify_buyer=1", req.Body);
    }

    [Fact]
    public async Task ProcessRefundAsync_Sandbox_Throws_AndMakesNoNetworkCall()
    {
        var (provider, requests) = MakeProvider(sandbox: true);

        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "PF-PAY-1",
            Amount = 100m,
            Reason = "x"
        }));

        Assert.Contains("sandbox", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(requests);
    }
}

/// <summary>Subscription update, pause-cycles, and frequency-mapping tests for PayFast.</summary>
public class PayFastSubscriptionUpdateTests
{
    private static (PayFastSubscriptionProvider Provider, List<(HttpMethod Method, string Uri, string Body)> Requests)
        MakeProvider()
    {
        var requests = new List<(HttpMethod, string, string)>();
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            requests.Add((req.Method, req.RequestUri!.ToString(), body));
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        });
        var provider = new PayFastSubscriptionProvider(
            new HttpClient(handler),
            Options.Create(new PayFastOptions { MerchantId = "10000100", Passphrase = "pp", UseSandbox = false }),
            NullLogger<PayFastSubscriptionProvider>.Instance,
            new PayFastPlanCache());
        return (provider, requests);
    }

    [Fact]
    public async Task UpdateSubscriptionAsync_SendsPatchUpdate_WithMappedFields()
    {
        var (provider, requests) = MakeProvider();

        await provider.UpdateSubscriptionAsync("TOKEN-1", new SubscriptionUpdateRequest
        {
            Amount = 50m,
            Interval = SubscriptionInterval.Weekly,
            RemainingCycles = 3
        });

        var upd = requests.Single(r => r.Uri.Contains("/update", StringComparison.Ordinal));
        Assert.Equal(HttpMethod.Patch, upd.Method);
        Assert.Contains("subscriptions/TOKEN-1/update", upd.Uri);
        Assert.Contains("amount=5000", upd.Body);    // R50.00 -> 5000 cents
        Assert.Contains("frequency=2", upd.Body);    // Weekly -> 2
        Assert.Contains("cycles=3", upd.Body);
    }

    [Fact]
    public async Task UpdateSubscriptionAsync_NoFields_Throws()
    {
        var (provider, _) = MakeProvider();

        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            provider.UpdateSubscriptionAsync("TOKEN-1", new SubscriptionUpdateRequest()));

        Assert.Contains("no fields", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateSubscriptionAsync_BiWeekly_ThrowsUnsupported()
    {
        var (provider, _) = MakeProvider();

        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            provider.UpdateSubscriptionAsync("TOKEN-1", new SubscriptionUpdateRequest { Interval = SubscriptionInterval.BiWeekly }));

        Assert.Contains("bi-weekly", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PauseSubscriptionAsync_WithCycles_SendsCyclesBody()
    {
        var (provider, requests) = MakeProvider();

        await provider.PauseSubscriptionAsync("TOKEN-1", cycles: 2);

        var pause = requests.Single(r => r.Uri.Contains("/pause", StringComparison.Ordinal));
        Assert.Equal(HttpMethod.Put, pause.Method);
        Assert.Contains("cycles=2", pause.Body);
    }

    [Fact]
    public async Task PauseSubscriptionAsync_ZeroCycles_Throws()
    {
        var (provider, _) = MakeProvider();
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.PauseSubscriptionAsync("TOKEN-1", cycles: 0));
    }
}
