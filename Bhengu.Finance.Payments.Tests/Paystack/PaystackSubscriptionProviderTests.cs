// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Internals;
using Bhengu.Finance.Payments.Paystack.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paystack;

public class PaystackSubscriptionProviderTests
{
    private static PaystackSubscriptionProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new PaystackOptions { SecretKey = "sk_test_xx", DefaultEmail = "buyer@example.com" }),
            NullLogger<PaystackSubscriptionProvider>.Instance,
            new PaystackIdempotencyCache());

    [Fact]
    public void Constructor_Throws_WhenSecretKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() => new PaystackSubscriptionProvider(
            http,
            Options.Create(new PaystackOptions()),
            NullLogger<PaystackSubscriptionProvider>.Instance,
            new PaystackIdempotencyCache()));
    }

    [Fact]
    public async Task CreatePlanAsync_ReturnsPlan_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("plan", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"data":{"name":"Pro","plan_code":"PLN_pro","amount":50000,"currency":"NGN","interval":"monthly","invoice_limit":12,"description":"Pro plan"}}
                """);
        });
        var provider = Create(handler);
        var plan = await provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Pro",
            Amount = 500m,
            Currency = "NGN",
            Interval = SubscriptionInterval.Monthly,
            TotalCycles = 12,
            Description = "Pro plan"
        });

        Assert.Equal("PLN_pro", plan.Reference);
        Assert.Equal(500m, plan.Amount);
        Assert.Equal(SubscriptionInterval.Monthly, plan.Interval);
        Assert.Equal(12, plan.TotalCycles);
    }

    [Fact]
    public async Task CreatePlanAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "slow down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.CreatePlanAsync(new PlanRequest
        {
            Name = "P", Amount = 1m, Currency = "NGN", Interval = SubscriptionInterval.Monthly
        }));
    }

    [Fact]
    public async Task CreatePlanAsync_Throws4xxAsDeclined()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad plan"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.CreatePlanAsync(new PlanRequest
        {
            Name = "P", Amount = 1m, Currency = "NGN", Interval = SubscriptionInterval.Monthly
        }));
    }

    [Fact]
    public async Task GetPlanAsync_ReturnsNull_On404()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "no plan"));
        var provider = Create(handler);
        Assert.Null(await provider.GetPlanAsync("PLN_missing"));
    }

    [Fact]
    public async Task CreateSubscriptionAsync_ReturnsSubscription_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("subscription", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"data":{"subscription_code":"SUB_abc","status":"active","createdAt":"2026-06-01T00:00:00Z","next_payment_date":"2026-07-01T00:00:00Z","quantity_charged":1,"plan":{"plan_code":"PLN_pro","name":"Pro","amount":50000,"currency":"NGN","interval":"monthly"},"customer":{"customer_code":"CUS_xyz","email":"buyer@example.com"}}}
                """);
        });
        var provider = Create(handler);
        var sub = await provider.CreateSubscriptionAsync(new SubscriptionRequest
        {
            PlanReference = "PLN_pro",
            CustomerId = "CUS_xyz",
            PaymentMethodToken = "AUTH_abc"
        });

        Assert.Equal("SUB_abc", sub.Reference);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Equal("PLN_pro", sub.PlanReference);
        Assert.Equal("CUS_xyz", sub.CustomerId);
        Assert.NotNull(sub.NextBillingAt);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_Throws5xxAsUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.CreateSubscriptionAsync(new SubscriptionRequest
        {
            PlanReference = "PLN", CustomerId = "CUS", PaymentMethodToken = "AUTH"
        }));
    }

    [Fact]
    public async Task GetSubscriptionAsync_ReturnsSubscription_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"status":true,"data":{"subscription_code":"SUB_xyz","status":"complete","plan":{"plan_code":"P"},"customer":{"customer_code":"C"},"quantity_charged":5}}
            """));
        var provider = Create(handler);
        var sub = await provider.GetSubscriptionAsync("SUB_xyz");
        Assert.NotNull(sub);
        Assert.Equal(SubscriptionStatus.Cancelled, sub!.Status);
        Assert.Equal(5, sub.CyclesCompleted);
    }
}
