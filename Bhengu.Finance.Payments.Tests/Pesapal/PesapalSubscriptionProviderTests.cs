// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.Pesapal.Configuration;
using Bhengu.Finance.Payments.Pesapal.Internals;
using Bhengu.Finance.Payments.Pesapal.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Pesapal;

public class PesapalSubscriptionProviderTests
{
    private static PesapalSubscriptionProvider Create(
        StubHttpMessageHandler handler,
        IBhenguDistributedCache? cache = null,
        PesapalTokenCache? tokenCache = null)
    {
        return new PesapalSubscriptionProvider(
            new HttpClient(handler),
            Options.Create(new PesapalOptions
            {
                ConsumerKey = "ck",
                ConsumerSecret = "cs",
                IpnId = "ipn-1",
                CallbackUrl = "https://merchant.example/cb",
                Currency = "KES"
            }),
            NullLogger<PesapalSubscriptionProvider>.Instance,
            cache,
            tokenCache);
    }

    [Fact]
    public void Constructor_Throws_WhenConsumerKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ProviderConfigurationException>(() => new PesapalSubscriptionProvider(
            http,
            Options.Create(new PesapalOptions { ConsumerSecret = "cs" }),
            NullLogger<PesapalSubscriptionProvider>.Instance));
    }

    [Fact]
    public async Task CreatePlanAsync_PersistsPlanToCache_AndReturnsIt()
    {
        var cache = new InMemoryBhenguDistributedCache();
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), cache);
        var plan = await provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Monthly Pro",
            Amount = 500m,
            Currency = "KES",
            Interval = SubscriptionInterval.Monthly,
            TotalCycles = 12
        });
        Assert.False(string.IsNullOrEmpty(plan.Reference));
        Assert.Equal("Monthly Pro", plan.Name);
        Assert.Equal(SubscriptionInterval.Monthly, plan.Interval);

        var fetched = await provider.GetPlanAsync(plan.Reference);
        Assert.NotNull(fetched);
        Assert.Equal(plan.Amount, fetched!.Amount);
    }

    [Fact]
    public async Task GetPlanAsync_ReturnsNull_ForUnknownReference()
    {
        var cache = new InMemoryBhenguDistributedCache();
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), cache);
        Assert.Null(await provider.GetPlanAsync("plan-missing-xyz"));
    }

    [Fact]
    public async Task CreateSubscriptionAsync_RequiresPlan_AndPostsSubmitOrderRequest()
    {
        var cache = new InMemoryBhenguDistributedCache();
        var tokenCache = new PesapalTokenCache();
        var calls = new List<string>();
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var path = req.RequestUri!.PathAndQuery;
            calls.Add(path);
            if (path.Contains("Auth/RequestToken"))
            {
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"tok-pesapal","expiryDate":"2026-06-04T00:00:00Z"}""");
            }
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"order_tracking_id":"otk-1","merchant_reference":"sub-1","redirect_url":"https://pay.pesapal.com/iframe","status":"200"}
                """);
        });
        var provider = Create(handler, cache, tokenCache);
        var plan = await provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Monthly", Amount = 500m, Currency = "KES",
            Interval = SubscriptionInterval.Monthly, TotalCycles = 12
        });

        var sub = await provider.CreateSubscriptionAsync(new SubscriptionRequest
        {
            PlanReference = plan.Reference,
            CustomerId = "buyer@example.com",
            PaymentMethodToken = "pmt-1"
        });
        Assert.Equal("otk-1", sub.Reference);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Equal("buyer@example.com", sub.CustomerId);
        Assert.Contains("SubmitOrderRequest", string.Join(',', calls));
    }

    [Fact]
    public async Task CreateSubscriptionAsync_Throws_WhenPlanNotFound()
    {
        var cache = new InMemoryBhenguDistributedCache();
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"tok","expiryDate":"2026-06-04T00:00:00Z"}"""));
        var provider = Create(handler, cache);
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.CreateSubscriptionAsync(new SubscriptionRequest
        {
            PlanReference = "missing",
            CustomerId = "buyer@example.com",
            PaymentMethodToken = "pmt-1"
        }));
    }

    [Fact]
    public async Task CancelSubscriptionAsync_PostsCancelOrder_AndReturnsCancelledStub()
    {
        var tokenCache = new PesapalTokenCache();
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.Contains("Auth/RequestToken"))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"tok","expiryDate":"2026-06-04T00:00:00Z"}""");
            Assert.Contains("CancelOrder", path);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"200","message":"cancelled"}""");
        });
        var provider = Create(handler, tokenCache: tokenCache);
        var sub = await provider.CancelSubscriptionAsync("otk-1");
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
        Assert.NotNull(sub.CancelledAt);
    }

    [Fact]
    public async Task PauseSubscriptionAsync_Throws_NotSupported()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.PauseSubscriptionAsync("any"));
    }

    [Fact]
    public async Task ResumeSubscriptionAsync_Throws_NotSupported()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.ResumeSubscriptionAsync("any"));
    }
}
