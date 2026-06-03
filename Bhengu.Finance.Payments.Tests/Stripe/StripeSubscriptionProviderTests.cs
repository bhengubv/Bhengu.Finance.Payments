// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Stripe;

public class StripeSubscriptionProviderTests
{
    private static StripeSubscriptionProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new StripeOptions { SecretKey = "sk_test_fake", WebhookSecret = "whsec_test_fake" }),
            NullLogger<StripeSubscriptionProvider>.Instance);

    [Fact]
    public async Task CreatePlanAsync_ReturnsMappedPlan()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("plans", req.RequestUri!.PathAndQuery, StringComparison.Ordinal);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"plan_pro","object":"plan","amount":9999,"currency":"usd","interval":"month","interval_count":1,"nickname":"Pro Monthly","active":true}
                """);
        });
        var provider = Create(handler);
        var plan = await provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Pro Monthly",
            Amount = 99.99m,
            Currency = "USD",
            Interval = SubscriptionInterval.Monthly
        });
        Assert.Equal("plan_pro", plan.Reference);
        Assert.Equal(99.99m, plan.Amount);
        Assert.Equal("USD", plan.Currency);
        Assert.Equal(SubscriptionInterval.Monthly, plan.Interval);
    }

    [Fact]
    public async Task CreatePlanAsync_Throws4xxAsPaymentDeclined()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, """
            {"error":{"type":"invalid_request_error","code":"resource_missing","message":"Missing required field amount"}}
            """));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Bad", Amount = 1m, Currency = "USD", Interval = SubscriptionInterval.Monthly
        }));
    }

    [Fact]
    public async Task GetPlanAsync_ReturnsNull_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.NotFound, """
            {"error":{"type":"invalid_request_error","message":"No such plan"}}
            """));
        var provider = Create(handler);
        Assert.Null(await provider.GetPlanAsync("plan_missing"));
    }

    [Fact]
    public async Task CreateSubscriptionAsync_ReturnsMappedSubscription()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("subscriptions", req.RequestUri!.PathAndQuery, StringComparison.Ordinal);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"sub_abc","object":"subscription","customer":"cus_abc","status":"active","start_date":1700000000,"current_period_end":1702592000,"items":{"object":"list","data":[{"id":"si_1","plan":{"id":"plan_pro","amount":9999,"currency":"usd","interval":"month","interval_count":1}}]}}
                """);
        });
        var provider = Create(handler);

        var sub = await provider.CreateSubscriptionAsync(new SubscriptionRequest
        {
            PlanReference = "plan_pro",
            CustomerId = "cus_abc",
            PaymentMethodToken = "pm_test"
        });
        Assert.Equal("sub_abc", sub.Reference);
        Assert.Equal("plan_pro", sub.PlanReference);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.NotNull(sub.NextBillingAt);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_DeclinedFirstCharge_ThrowsPaymentDeclined()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.PaymentRequired, """
            {"error":{"type":"card_error","code":"card_declined","message":"Card declined"}}
            """));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.CreateSubscriptionAsync(new SubscriptionRequest
        {
            PlanReference = "plan_pro", CustomerId = "cus_abc", PaymentMethodToken = "pm_test"
        }));
    }

    [Fact]
    public async Task CancelSubscriptionAsync_Immediately_CallsCancelEndpoint()
    {
        var sawCancel = false;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Delete) sawCancel = true;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"sub_abc","object":"subscription","customer":"cus_abc","status":"canceled","start_date":1700000000,"canceled_at":1701000000,"items":{"object":"list","data":[{"id":"si_1","plan":{"id":"plan_pro"}}]}}
                """);
        });
        var provider = Create(handler);
        var sub = await provider.CancelSubscriptionAsync("sub_abc", immediately: true);
        Assert.True(sawCancel);
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_AtPeriodEnd_UsesUpdateNotDelete()
    {
        var sawDelete = false;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Delete) sawDelete = true;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"sub_abc","object":"subscription","customer":"cus_abc","status":"active","cancel_at_period_end":true,"start_date":1700000000,"current_period_end":1702592000,"items":{"object":"list","data":[{"id":"si_1","plan":{"id":"plan_pro"}}]}}
                """);
        });
        var provider = Create(handler);
        var sub = await provider.CancelSubscriptionAsync("sub_abc", immediately: false);
        Assert.False(sawDelete);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
    }

    [Fact]
    public async Task PauseSubscriptionAsync_PostsPauseCollection()
    {
        var bodyHadPause = false;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.Content is not null)
            {
                var body = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                bodyHadPause = body.Contains("pause_collection", StringComparison.Ordinal);
            }
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"sub_abc","object":"subscription","customer":"cus_abc","status":"active","pause_collection":{"behavior":"mark_uncollectible"},"start_date":1700000000,"items":{"object":"list","data":[{"id":"si_1","plan":{"id":"plan_pro"}}]}}
                """);
        });
        var provider = Create(handler);
        var sub = await provider.PauseSubscriptionAsync("sub_abc");
        Assert.True(bodyHadPause);
        Assert.NotNull(sub);
    }
}
