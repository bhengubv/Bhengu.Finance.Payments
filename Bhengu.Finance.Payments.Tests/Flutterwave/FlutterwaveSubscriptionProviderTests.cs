// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Bhengu.Finance.Payments.Flutterwave.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Flutterwave;

public class FlutterwaveSubscriptionProviderTests
{
    private static FlutterwaveSubscriptionProvider Create(StubHttpMessageHandler handler, FlutterwaveOptions? opts = null)
    {
        opts ??= new FlutterwaveOptions
        {
            SecretKey = "FLWSECK_TEST-xxx",
            RedirectUrl = "https://example.com/return"
        };
        var http = new HttpClient(handler);
        return new FlutterwaveSubscriptionProvider(http, Options.Create(opts), NullLogger<FlutterwaveSubscriptionProvider>.Instance);
    }

    [Fact]
    public async Task CreatePlanAsync_ReturnsPlan_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v3/payment-plans", req.RequestUri!.PathAndQuery);
            Assert.Equal(HttpMethod.Post, req.Method);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","message":"Plan created","data":{"id":4321,"name":"Pro Monthly","amount":99.99,"currency":"NGN","interval":"monthly","duration":12,"status":"active"}}
                """);
        });
        var provider = Create(handler);

        var plan = await provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Pro Monthly",
            Amount = 99.99m,
            Currency = "NGN",
            Interval = SubscriptionInterval.Monthly,
            TotalCycles = 12
        });

        Assert.Equal("4321", plan.Reference);
        Assert.Equal("Pro Monthly", plan.Name);
        Assert.Equal(SubscriptionInterval.Monthly, plan.Interval);
        Assert.Equal(12, plan.TotalCycles);
    }

    [Fact]
    public async Task CreatePlanAsync_Wraps4xxAsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "invalid currency"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.CreatePlanAsync(new PlanRequest
        {
            Name = "x", Amount = 1m, Currency = "XYZ", Interval = SubscriptionInterval.Monthly
        }));
    }

    [Fact]
    public async Task GetPlanAsync_ReturnsNull_WhenNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "no such plan"));
        var provider = Create(handler);
        Assert.Null(await provider.GetPlanAsync("does-not-exist"));
    }

    [Fact]
    public async Task GetPlanAsync_ReturnsPlan_WhenFound()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","data":{"id":4321,"name":"Pro Monthly","amount":99.99,"currency":"NGN","interval":"monthly","duration":12,"status":"active"}}
                """);
        });
        var provider = Create(handler);
        var plan = await provider.GetPlanAsync("4321");
        Assert.NotNull(plan);
        Assert.Equal("4321", plan!.Reference);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_ReturnsActiveSubscription_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v3/payments", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","message":"Hosted Link","data":{"link":"https://checkout.flutterwave.com/v3/hosted/pay/abc"}}
                """);
        });
        var provider = Create(handler);

        var sub = await provider.CreateSubscriptionAsync(new SubscriptionRequest
        {
            PlanReference = "4321",
            CustomerId = "buyer@example.com",
            PaymentMethodToken = "flw-tok_abc"
        });

        Assert.Equal("4321", sub.PlanReference);
        Assert.Equal("buyer@example.com", sub.CustomerId);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.StartsWith("sub-", sub.Reference);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_CallsCancelEndpoint()
    {
        var path = string.Empty;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            path = req.RequestUri!.PathAndQuery;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","data":{"id":9999,"status":"cancelled","plan":4321,"customer":"buyer@example.com"}}
                """);
        });
        var provider = Create(handler);

        var sub = await provider.CancelSubscriptionAsync("9999");
        Assert.Contains("v3/subscriptions/9999/cancel", path);
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_Immediately_CallsPaymentPlanCancelEndpoint()
    {
        var path = string.Empty;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            path = req.RequestUri!.PathAndQuery;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","data":{"id":4321,"status":"cancelled"}}
                """);
        });
        var provider = Create(handler);

        var sub = await provider.CancelSubscriptionAsync("4321", immediately: true);
        Assert.Contains("v3/payment-plans/4321/cancel", path);
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_Idempotent_When404()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "gone"));
        var provider = Create(handler);

        var sub = await provider.CancelSubscriptionAsync("missing");
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
        Assert.Equal("missing", sub.Reference);
    }

    [Fact]
    public async Task PauseSubscriptionAsync_Throws_NotSupported()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.PauseSubscriptionAsync("sub-1"));
    }

    [Fact]
    public async Task ResumeSubscriptionAsync_Throws_NotSupported()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.ResumeSubscriptionAsync("sub-1"));
    }
}
