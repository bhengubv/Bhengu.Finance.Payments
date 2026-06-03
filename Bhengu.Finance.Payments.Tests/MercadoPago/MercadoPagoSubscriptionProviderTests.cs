// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.MercadoPago.Configuration;
using Bhengu.Finance.Payments.MercadoPago.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.MercadoPago;

public class MercadoPagoSubscriptionProviderTests
{
    private static MercadoPagoSubscriptionProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new MercadoPagoOptions { AccessToken = "TEST-token" }),
            NullLogger<MercadoPagoSubscriptionProvider>.Instance);

    private static PlanRequest SamplePlan() => new()
    {
        Name = "Pro Mensal",
        Amount = 99.90m,
        Currency = "BRL",
        Interval = SubscriptionInterval.Monthly,
        Description = "Plano profissional"
    };

    [Fact]
    public async Task CreatePlanAsync_CachesPlanInProcess_AndIssuesNoHttpCall()
    {
        var callCount = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            callCount++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        });
        var provider = Create(handler);

        var plan = await provider.CreatePlanAsync(SamplePlan());

        Assert.StartsWith("mp_plan_", plan.Reference, StringComparison.Ordinal);
        Assert.Equal("Pro Mensal", plan.Name);
        Assert.Equal(99.90m, plan.Amount);
        Assert.Equal(SubscriptionInterval.Monthly, plan.Interval);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task GetPlanAsync_ReturnsCachedPlan()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);

        var plan = await provider.CreatePlanAsync(SamplePlan());
        var fetched = await provider.GetPlanAsync(plan.Reference);

        Assert.NotNull(fetched);
        Assert.Equal(plan.Reference, fetched!.Reference);
    }

    [Fact]
    public async Task GetPlanAsync_ReturnsNull_WhenMissing()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        Assert.Null(await provider.GetPlanAsync("mp_plan_unknown"));
    }

    [Fact]
    public async Task CreateSubscriptionAsync_PostsPreapproval_WithAutoRecurring()
    {
        string? sentBody = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/preapproval", req.RequestUri!.PathAndQuery);
            sentBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.Created, """
                {"id":"pa-abc-123","status":"authorized","payer_id":12345,"date_created":"2026-06-03T10:00:00.000-03:00","auto_recurring":{"frequency":1,"frequency_type":"months","transaction_amount":99.90,"currency_id":"BRL","payments_count":0}}
                """);
        });
        var provider = Create(handler);

        var plan = await provider.CreatePlanAsync(SamplePlan());
        var sub = await provider.CreateSubscriptionAsync(new SubscriptionRequest
        {
            PlanReference = plan.Reference,
            CustomerId = "cust_1",
            PaymentMethodToken = "card_token_x",
            Metadata = new Dictionary<string, string> { ["payer_email"] = "user@example.com" }
        });

        Assert.Equal("pa-abc-123", sub.Reference);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.NotNull(sentBody);
        Assert.Contains("\"frequency\":1", sentBody!);
        Assert.Contains("\"frequency_type\":\"months\"", sentBody);
        Assert.Contains("\"transaction_amount\":99.90", sentBody);
        Assert.Contains("\"currency_id\":\"BRL\"", sentBody);
        Assert.Contains("\"status\":\"authorized\"", sentBody);
        Assert.Contains("\"card_token_id\":\"card_token_x\"", sentBody);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_Throws_WhenPlanNotCached()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            provider.CreateSubscriptionAsync(new SubscriptionRequest
            {
                PlanReference = "mp_plan_does_not_exist",
                CustomerId = "cust_1",
                PaymentMethodToken = "token_x"
            }));
        Assert.Equal("unknown_plan", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task GetSubscriptionAsync_ReturnsSubscription()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains("/preapproval/pa-1", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"pa-1","status":"authorized","payer_id":987,"preapproval_plan_id":"plan-1","date_created":"2026-06-01T10:00:00.000-03:00","next_payment_date":"2026-07-01T10:00:00.000-03:00","auto_recurring":{"payments_count":2}}
                """);
        });
        var provider = Create(handler);

        var sub = await provider.GetSubscriptionAsync("pa-1");
        Assert.NotNull(sub);
        Assert.Equal("pa-1", sub!.Reference);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Equal(2, sub.CyclesCompleted);
        Assert.NotNull(sub.NextBillingAt);
    }

    [Fact]
    public async Task GetSubscriptionAsync_ReturnsNull_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.Null(await provider.GetSubscriptionAsync("missing"));
    }

    [Fact]
    public async Task CancelSubscriptionAsync_PutsStatusCancelled()
    {
        string? sentBody = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Put, req.Method);
            Assert.Contains("/preapproval/pa-1", req.RequestUri!.PathAndQuery);
            sentBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"pa-1","status":"cancelled","last_modified":"2026-06-03T10:00:00.000-03:00"}""");
        });
        var provider = Create(handler);

        var sub = await provider.CancelSubscriptionAsync("pa-1");
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
        Assert.NotNull(sentBody);
        Assert.Contains("\"status\":\"cancelled\"", sentBody!);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_IsIdempotent_WhenAlreadyCancelled()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            calls++;
            if (req.Method == HttpMethod.Put)
                return StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "already cancelled");
            // Fallback Get returns a cancelled subscription so the provider can treat the 400 as idempotent.
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"pa-1","status":"cancelled"}""");
        });
        var provider = Create(handler);
        var sub = await provider.CancelSubscriptionAsync("pa-1");

        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PauseSubscriptionAsync_PutsStatusPaused()
    {
        string? sentBody = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Put, req.Method);
            sentBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"pa-1","status":"paused"}""");
        });
        var provider = Create(handler);
        var sub = await provider.PauseSubscriptionAsync("pa-1");
        Assert.Equal(SubscriptionStatus.Paused, sub.Status);
        Assert.NotNull(sentBody);
        Assert.Contains("\"status\":\"paused\"", sentBody!);
    }

    [Fact]
    public async Task ResumeSubscriptionAsync_PutsStatusAuthorized()
    {
        string? sentBody = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            sentBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"pa-1","status":"authorized"}""");
        });
        var provider = Create(handler);
        var sub = await provider.ResumeSubscriptionAsync("pa-1");
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.NotNull(sentBody);
        Assert.Contains("\"status\":\"authorized\"", sentBody!);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_Throws_OnRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "throttled"));
        var provider = Create(handler);
        var plan = await provider.CreatePlanAsync(SamplePlan());

        await Assert.ThrowsAsync<ProviderRateLimitException>(() =>
            provider.CreateSubscriptionAsync(new SubscriptionRequest
            {
                PlanReference = plan.Reference,
                CustomerId = "c",
                PaymentMethodToken = "t"
            }));
    }

    [Fact]
    public async Task CreateSubscriptionAsync_Throws_On5xx()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
        var provider = Create(handler);
        var plan = await provider.CreatePlanAsync(SamplePlan());

        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            provider.CreateSubscriptionAsync(new SubscriptionRequest
            {
                PlanReference = plan.Reference,
                CustomerId = "c",
                PaymentMethodToken = "t"
            }));
    }
}
