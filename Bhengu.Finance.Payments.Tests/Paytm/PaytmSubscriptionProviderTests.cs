// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.Paytm.Configuration;
using Bhengu.Finance.Payments.Paytm.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paytm;

public class PaytmSubscriptionProviderTests
{
    private static PaytmSubscriptionProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new PaytmOptions { MerchantId = "MID1", MerchantKey = "secret_key" }),
            NullLogger<PaytmSubscriptionProvider>.Instance);

    [Fact]
    public async Task CreatePlanAsync_ReturnsCachedPlan()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        var plan = await provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Monthly",
            Amount = 199m,
            Currency = "INR",
            Interval = SubscriptionInterval.Monthly,
            TotalCycles = 12
        });
        Assert.StartsWith("paytm_plan_", plan.Reference);
        Assert.Equal("INR", plan.Currency);
    }

    [Fact]
    public async Task GetPlanAsync_ReturnsCachedPlan_AfterCreate()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        var plan = await provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Daily",
            Amount = 10m,
            Currency = "INR",
            Interval = SubscriptionInterval.Daily
        });
        var fetched = await provider.GetPlanAsync(plan.Reference);
        Assert.NotNull(fetched);
        Assert.Equal(plan.Amount, fetched!.Amount);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_PostsToCreate_AndReturnsActive()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("subscription/create", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"body":{"resultInfo":{"resultStatus":"S"},"subscriptionId":"SUB1"}}
                """);
        });
        var provider = Create(handler);
        var plan = await provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Monthly",
            Amount = 199m,
            Currency = "INR",
            Interval = SubscriptionInterval.Monthly
        });

        var sub = await provider.CreateSubscriptionAsync(new SubscriptionRequest
        {
            PlanReference = plan.Reference,
            CustomerId = "cust1",
            PaymentMethodToken = "tkn1"
        });
        Assert.Equal("SUB1", sub.Reference);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_Throws_WhenPlanMissing()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.CreateSubscriptionAsync(new SubscriptionRequest
        {
            PlanReference = "paytm_plan_missing",
            CustomerId = "c",
            PaymentMethodToken = "t"
        }));
    }

    [Fact]
    public async Task GetSubscriptionAsync_DeserialisesRemote()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"body":{"subscriptionId":"SUB1","planId":"p","customerId":"c","status":"active","cyclesCompleted":2}}
                """));
        var provider = Create(handler);
        var sub = await provider.GetSubscriptionAsync("SUB1");
        Assert.NotNull(sub);
        Assert.Equal("SUB1", sub!.Reference);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Equal(2, sub.CyclesCompleted);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_ReturnsCancelled()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("subscription/cancelSubscription", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"body":{"resultInfo":{"resultStatus":"S"}}}""");
        });
        var provider = Create(handler);
        var sub = await provider.CancelSubscriptionAsync("SUB1");
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
    }

    [Fact]
    public async Task PauseSubscriptionAsync_Throws_BecausePaytmUnsupported()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.PauseSubscriptionAsync("SUB1"));
    }
}
