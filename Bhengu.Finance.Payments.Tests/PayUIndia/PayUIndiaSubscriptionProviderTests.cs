// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.PayUIndia.Configuration;
using Bhengu.Finance.Payments.PayUIndia.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayUIndia;

public class PayUIndiaSubscriptionProviderTests
{
    private static PayUIndiaSubscriptionProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new PayUIndiaOptions
            {
                MerchantKey = "gtKFFx",
                Salt = "eCwWELxi"
            }),
            NullLogger<PayUIndiaSubscriptionProvider>.Instance);

    [Fact]
    public async Task CreatePlanAsync_CachesLocally_AndReturnsReference()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        var plan = await provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Pro Monthly",
            Amount = 499m,
            Currency = "INR",
            Interval = SubscriptionInterval.Monthly,
            TotalCycles = 12,
            Description = "Pro"
        });
        Assert.StartsWith("payu_plan_", plan.Reference);
        Assert.Equal(499m, plan.Amount);
        Assert.Equal("INR", plan.Currency);
        Assert.Equal(12, plan.TotalCycles);
    }

    [Fact]
    public async Task GetPlanAsync_ReturnsPlan_AfterCreate()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        var plan = await provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Pro",
            Amount = 100m,
            Currency = "INR",
            Interval = SubscriptionInterval.Weekly
        });
        var fetched = await provider.GetPlanAsync(plan.Reference);
        Assert.NotNull(fetched);
        Assert.Equal(plan.Reference, fetched!.Reference);
    }

    [Fact]
    public async Task GetPlanAsync_ReturnsNull_WhenNotFound()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Null(await provider.GetPlanAsync("payu_plan_missing"));
    }

    [Fact]
    public async Task CreateSubscriptionAsync_PostsSiCreate_AndReturnsActive()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("merchant/postservice.php", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"1","si_id":"si_remote_1"}
                """);
        });
        var provider = Create(handler);

        var plan = await provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Monthly",
            Amount = 100m,
            Currency = "INR",
            Interval = SubscriptionInterval.Monthly
        });
        var sub = await provider.CreateSubscriptionAsync(new SubscriptionRequest
        {
            PlanReference = plan.Reference,
            CustomerId = "cust_1",
            PaymentMethodToken = "tkn_1"
        });

        Assert.Equal("si_remote_1", sub.Reference);
        Assert.Equal("cust_1", sub.CustomerId);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.NotNull(sub.NextBillingAt);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_Throws_WhenPlanNotCached()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.CreateSubscriptionAsync(new SubscriptionRequest
        {
            PlanReference = "payu_plan_missing",
            CustomerId = "cust_1",
            PaymentMethodToken = "tkn_1"
        }));
    }

    [Fact]
    public async Task GetSubscriptionAsync_DeserialisesRemote()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"active","si_id":"si_remote_2","plan_id":"plan_x","customer_id":"cust_1","cycles_completed":3}
                """));
        var provider = Create(handler);

        var sub = await provider.GetSubscriptionAsync("si_remote_2");

        Assert.NotNull(sub);
        Assert.Equal("si_remote_2", sub!.Reference);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Equal(3, sub.CyclesCompleted);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_PostsCancel_AndReturnsCancelled()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"1","si_id":"si_remote_3"}"""));
        var provider = Create(handler);
        var sub = await provider.CancelSubscriptionAsync("si_remote_3");
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
        Assert.NotNull(sub.CancelledAt);
    }

    [Fact]
    public async Task PauseSubscriptionAsync_Throws_BecausePayUIndiaUnsupported()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.PauseSubscriptionAsync("any"));
    }
}
