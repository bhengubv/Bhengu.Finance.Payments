// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.PayJustNow.Configuration;
using Bhengu.Finance.Payments.PayJustNow.Internals;
using Bhengu.Finance.Payments.PayJustNow.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayJustNow;

public class PayJustNowSubscriptionProviderTests
{
    private static PayJustNowSubscriptionProvider Create(StubHttpMessageHandler handler)
    {
        var opts = new PayJustNowOptions { ApiKey = "k", MerchantId = "m", UseSandbox = true };
        var http = new HttpClient(handler);
        return new PayJustNowSubscriptionProvider(http, Options.Create(opts), NullLogger<PayJustNowSubscriptionProvider>.Instance,
            new PayJustNowIdempotencyCache(new InMemoryBhenguDistributedCache()));
    }

    [Fact]
    public async Task CreatePlanAsync_MapsResponse()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"id":"plan_1","name":"Pro","amount":99900,"currency":"ZAR","interval":"monthly","instalment_count":12}"""));
        var provider = Create(handler);
        var plan = await provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Pro",
            Amount = 999m,
            Currency = "ZAR",
            Interval = SubscriptionInterval.Monthly,
            TotalCycles = 12
        });
        Assert.Equal("plan_1", plan.Reference);
        Assert.Equal(999m, plan.Amount);
        Assert.Equal(SubscriptionInterval.Monthly, plan.Interval);
        Assert.Equal(12, plan.TotalCycles);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_MapsResponse()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"id":"sub_1","plan_id":"plan_1","shopper_reference":"shop-1","status":"active","started_at":"2026-06-04T00:00:00Z"}"""));
        var provider = Create(handler);
        var sub = await provider.CreateSubscriptionAsync(new SubscriptionRequest
        {
            PlanReference = "plan_1",
            CustomerId = "shop-1",
            PaymentMethodToken = "tok"
        });
        Assert.Equal("sub_1", sub.Reference);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
    }

    [Fact]
    public async Task GetSubscriptionAsync_Returns404AsNull()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "no"));
        var provider = Create(handler);
        Assert.Null(await provider.GetSubscriptionAsync("missing"));
    }

    [Fact]
    public async Task CancelSubscriptionAsync_ReturnsCancelled()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"id":"sub_1","plan_id":"plan_1","status":"cancelled"}"""));
        var provider = Create(handler);
        var sub = await provider.CancelSubscriptionAsync("sub_1");
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
    }

    [Fact]
    public async Task PauseSubscriptionAsync_ReturnsPaused()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"id":"sub_1","plan_id":"plan_1","status":"paused"}"""));
        var provider = Create(handler);
        var sub = await provider.PauseSubscriptionAsync("sub_1");
        Assert.Equal(SubscriptionStatus.Paused, sub.Status);
    }

    [Fact]
    public async Task CreatePlanAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.CreatePlanAsync(new PlanRequest
        {
            Name = "x", Amount = 1m, Currency = "ZAR", Interval = SubscriptionInterval.Monthly
        }));
    }
}
