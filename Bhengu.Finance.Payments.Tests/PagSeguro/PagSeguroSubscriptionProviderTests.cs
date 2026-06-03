// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.PagSeguro.Configuration;
using Bhengu.Finance.Payments.PagSeguro.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PagSeguro;

public class PagSeguroSubscriptionProviderTests
{
    private static PagSeguroSubscriptionProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new PagSeguroOptions { ApiToken = "pagbank-test-token" }),
            NullLogger<PagSeguroSubscriptionProvider>.Instance);

    private static PlanRequest SamplePlan() => new()
    {
        Name = "Pro Mensal",
        Amount = 99.90m,
        Currency = "BRL",
        Interval = SubscriptionInterval.Monthly,
        Description = "Plano profissional",
        TotalCycles = 12
    };

    [Fact]
    public async Task CreatePlanAsync_CachesPlanInProcess_AndIssuesNoHttpCall()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        });
        var provider = Create(handler);

        var plan = await provider.CreatePlanAsync(SamplePlan());

        Assert.StartsWith("pg_plan_", plan.Reference, StringComparison.Ordinal);
        Assert.Equal("Pro Mensal", plan.Name);
        Assert.Equal(99.90m, plan.Amount);
        Assert.Equal(0, calls);
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
    public async Task CreateSubscriptionAsync_PostsToRecurringOrders_WithInlinePlan()
    {
        string? sentBody = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/recurring/orders", req.RequestUri!.PathAndQuery);
            sentBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.Created, """
                {"id":"RECU_abc123","reference_id":"pg_plan_x","status":"ACTIVE","customer":{"id":"CUST_1"},"start_date":"2026-06-03T10:00:00-03:00","next_invoice_at":"2026-07-03T10:00:00-03:00","cycles":{"completed":0,"total":12}}
                """);
        });
        var provider = Create(handler);

        var plan = await provider.CreatePlanAsync(SamplePlan());
        var sub = await provider.CreateSubscriptionAsync(new SubscriptionRequest
        {
            PlanReference = plan.Reference,
            CustomerId = "CUST_1",
            PaymentMethodToken = "ENC_CARD_TOK",
            Metadata = new Dictionary<string, string>
            {
                ["customer_email"] = "joao@example.com",
                ["customer_name"] = "Joao Silva"
            }
        });

        Assert.Equal("RECU_abc123", sub.Reference);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Equal("CUST_1", sub.CustomerId);
        Assert.NotNull(sentBody);
        Assert.Contains("\"interval\":\"MONTH\"", sentBody!);
        Assert.Contains("\"interval_count\":1", sentBody);
        Assert.Contains("\"value\":9990", sentBody);
        Assert.Contains("\"encrypted\":\"ENC_CARD_TOK\"", sentBody);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_Throws_WhenPlanNotCached()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            provider.CreateSubscriptionAsync(new SubscriptionRequest
            {
                PlanReference = "pg_plan_missing",
                CustomerId = "x",
                PaymentMethodToken = "y",
                Metadata = new Dictionary<string, string> { ["customer_email"] = "a@b.c" }
            }));
        Assert.Equal("unknown_plan", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_Throws_WhenCustomerEmailMissing()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        var plan = await provider.CreatePlanAsync(SamplePlan());

        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            provider.CreateSubscriptionAsync(new SubscriptionRequest
            {
                PlanReference = plan.Reference,
                CustomerId = "x",
                PaymentMethodToken = "y"
            }));
        Assert.Equal("missing_customer_email", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task GetSubscriptionAsync_ReturnsSubscription()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains("/recurring/orders/RECU_1", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"RECU_1","reference_id":"pg_plan_x","status":"ACTIVE","customer":{"id":"CUST_1"},"cycles":{"completed":3,"total":12}}
                """);
        });
        var provider = Create(handler);

        var sub = await provider.GetSubscriptionAsync("RECU_1");
        Assert.NotNull(sub);
        Assert.Equal("RECU_1", sub!.Reference);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Equal(3, sub.CyclesCompleted);
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
    public async Task CancelSubscriptionAsync_HitsCancelEndpoint()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/recurring/orders/RECU_1/cancel", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"RECU_1","status":"CANCELED","customer":{"id":"CUST_1"}}
                """);
        });
        var provider = Create(handler);
        var sub = await provider.CancelSubscriptionAsync("RECU_1");
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_IsIdempotent_WhenAlreadyCancelled()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            calls++;
            if (req.Method == HttpMethod.Post)
                return StubHttpMessageHandler.Text(HttpStatusCode.UnprocessableEntity, "already cancelled");
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"RECU_1","status":"CANCELED"}""");
        });
        var provider = Create(handler);
        var sub = await provider.CancelSubscriptionAsync("RECU_1");
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PauseSubscriptionAsync_Throws_NotSupported()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            provider.PauseSubscriptionAsync("RECU_1"));
        Assert.Equal("pause_not_supported", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task ResumeSubscriptionAsync_Throws_NotSupported()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            provider.ResumeSubscriptionAsync("RECU_1"));
        Assert.Equal("resume_not_supported", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_Throws_On5xx()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.ServiceUnavailable, "down"));
        var provider = Create(handler);
        var plan = await provider.CreatePlanAsync(SamplePlan());
        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            provider.CreateSubscriptionAsync(new SubscriptionRequest
            {
                PlanReference = plan.Reference,
                CustomerId = "x",
                PaymentMethodToken = "y",
                Metadata = new Dictionary<string, string> { ["customer_email"] = "a@b.c" }
            }));
    }
}
