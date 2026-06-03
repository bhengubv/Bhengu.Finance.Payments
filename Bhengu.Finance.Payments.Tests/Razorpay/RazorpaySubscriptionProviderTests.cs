// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Bhengu.Finance.Payments.Razorpay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Razorpay;

public class RazorpaySubscriptionProviderTests
{
    private static RazorpaySubscriptionProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new RazorpayOptions { KeyId = "rzp_test_x", KeySecret = "secret_x" }),
            NullLogger<RazorpaySubscriptionProvider>.Instance);

    [Fact]
    public async Task CreatePlanAsync_PostsCorrectShape_AndReturnsPlan()
    {
        string? sentBody = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("v1/plans", req.RequestUri!.PathAndQuery);
            sentBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"plan_abc","entity":"plan","interval":1,"period":"monthly","item":{"name":"Pro","amount":99900,"currency":"INR"}}""");
        });

        var provider = Create(handler);
        var plan = await provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Pro",
            Amount = 999m,
            Currency = "INR",
            Interval = SubscriptionInterval.Monthly
        });

        Assert.Equal("plan_abc", plan.Reference);
        Assert.Equal("Pro", plan.Name);
        Assert.Equal(999m, plan.Amount);
        Assert.Equal(SubscriptionInterval.Monthly, plan.Interval);
        Assert.NotNull(sentBody);
        Assert.Contains("\"period\":\"monthly\"", sentBody!);
        Assert.Contains("\"amount\":99900", sentBody);
    }

    [Fact]
    public async Task CreatePlanAsync_MapsQuarterlyToMonthlyTimes3()
    {
        string? sentBody = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            sentBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"plan_q","entity":"plan","interval":3,"period":"monthly","item":{"name":"Q","amount":50000,"currency":"INR"}}""");
        });
        var provider = Create(handler);
        var plan = await provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Q",
            Amount = 500m,
            Currency = "INR",
            Interval = SubscriptionInterval.Quarterly
        });

        Assert.Equal(SubscriptionInterval.Quarterly, plan.Interval);
        Assert.NotNull(sentBody);
        Assert.Contains("\"period\":\"monthly\"", sentBody!);
        Assert.Contains("\"interval\":3", sentBody);
    }

    [Fact]
    public async Task CreatePlanAsync_PassesIdempotencyKey()
    {
        string? header = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            header = req.Headers.TryGetValues("X-Razorpay-IdempotencyKey", out var v) ? string.Join(",", v) : null;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"plan_i","entity":"plan","interval":1,"period":"monthly","item":{"name":"I","amount":100,"currency":"INR"}}""");
        });
        var provider = Create(handler);
        await provider.CreatePlanAsync(new PlanRequest
        {
            Name = "I",
            Amount = 1m,
            Currency = "INR",
            Interval = SubscriptionInterval.Monthly,
            IdempotencyKey = "idem-abc"
        });

        Assert.Equal("idem-abc", header);
    }

    [Fact]
    public async Task GetPlanAsync_ReturnsNull_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "not found"));
        var provider = Create(handler);
        Assert.Null(await provider.GetPlanAsync("missing"));
    }

    [Fact]
    public async Task CreateSubscriptionAsync_PostsAndReturns()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v1/subscriptions", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"sub_1","entity":"subscription","plan_id":"plan_abc","customer_id":"cust_1","status":"active","total_count":12,"paid_count":1,"charge_at":1700000000,"start_at":1690000000}
                """);
        });
        var provider = Create(handler);
        var sub = await provider.CreateSubscriptionAsync(new SubscriptionRequest
        {
            PlanReference = "plan_abc",
            CustomerId = "cust_1",
            PaymentMethodToken = "token_x"
        });

        Assert.Equal("sub_1", sub.Reference);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Equal("plan_abc", sub.PlanReference);
        Assert.Equal("cust_1", sub.CustomerId);
        Assert.Equal(1, sub.CyclesCompleted);
        Assert.NotNull(sub.NextBillingAt);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_PassesCorrectFlag_ForImmediate()
    {
        string? body = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("/cancel", req.RequestUri!.PathAndQuery);
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"sub_1","entity":"subscription","plan_id":"plan_abc","customer_id":"cust_1","status":"cancelled"}""");
        });
        var provider = Create(handler);
        var sub = await provider.CancelSubscriptionAsync("sub_1", immediately: true);
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
        Assert.NotNull(body);
        Assert.Contains("\"cancel_at_cycle_end\":0", body!);
    }

    [Fact]
    public async Task PauseSubscriptionAsync_HitsPauseEndpoint()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("/pause", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"sub_1","entity":"subscription","plan_id":"plan_abc","customer_id":"cust_1","status":"paused"}""");
        });
        var provider = Create(handler);
        var sub = await provider.PauseSubscriptionAsync("sub_1");
        Assert.Equal(SubscriptionStatus.Paused, sub.Status);
    }

    [Fact]
    public async Task ResumeSubscriptionAsync_HitsResumeEndpoint()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("/resume", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"sub_1","entity":"subscription","plan_id":"plan_abc","customer_id":"cust_1","status":"active"}""");
        });
        var provider = Create(handler);
        var sub = await provider.ResumeSubscriptionAsync("sub_1");
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
    }

    [Fact]
    public async Task CreatePlanAsync_OnNetworkError_ThrowsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.CreatePlanAsync(new PlanRequest
        {
            Name = "X",
            Amount = 1m,
            Currency = "INR",
            Interval = SubscriptionInterval.Monthly
        }));
    }

    [Fact]
    public async Task CreatePlanAsync_OnRateLimit_ThrowsProviderRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "throttled"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.CreatePlanAsync(new PlanRequest
        {
            Name = "X",
            Amount = 1m,
            Currency = "INR",
            Interval = SubscriptionInterval.Monthly
        }));
    }
}
