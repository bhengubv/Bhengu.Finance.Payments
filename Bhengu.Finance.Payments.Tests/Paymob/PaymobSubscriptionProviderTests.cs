// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.Paymob.Configuration;
using Bhengu.Finance.Payments.Paymob.Internals;
using Bhengu.Finance.Payments.Paymob.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paymob;

public class PaymobSubscriptionProviderTests
{
    private static PaymobSubscriptionProvider Create(StubHttpMessageHandler handler)
    {
        var opts = new PaymobOptions { ApiKey = "k", IntegrationId = 1, Currency = "EGP" };
        var http = new HttpClient(handler);
        return new PaymobSubscriptionProvider(http, Options.Create(opts), NullLogger<PaymobSubscriptionProvider>.Instance,
            new PaymobIdempotencyCache(new InMemoryBhenguDistributedCache()));
    }

    [Fact]
    public async Task CreatePlanAsync_MapsResponse()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("auth/tokens", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"t"}""");
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":111,"name":"Pro","amount_cents":99900,"currency":"EGP","frequency_unit":"month","frequency_value":1,"number_of_deductions":12}""");
        });
        var provider = Create(handler);
        var plan = await provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Pro",
            Amount = 999m,
            Currency = "EGP",
            Interval = SubscriptionInterval.Monthly,
            TotalCycles = 12
        });
        Assert.Equal("111", plan.Reference);
        Assert.Equal(999m, plan.Amount);
        Assert.Equal(SubscriptionInterval.Monthly, plan.Interval);
        Assert.Equal(12, plan.TotalCycles);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_MapsResponse()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("auth/tokens", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"t"}""");
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":222,"plan_id":111,"identifier":"cust-1","state":"active","start_date":"2026-06-04T00:00:00Z"}""");
        });
        var provider = Create(handler);
        var sub = await provider.CreateSubscriptionAsync(new SubscriptionRequest
        {
            PlanReference = "111",
            CustomerId = "cust-1",
            PaymentMethodToken = "CT_x"
        });
        Assert.Equal("222", sub.Reference);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
    }

    [Fact]
    public async Task GetSubscriptionAsync_Returns404AsNull()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("auth/tokens", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"t"}""");
            return StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "not found");
        });
        var provider = Create(handler);
        Assert.Null(await provider.GetSubscriptionAsync("missing"));
    }

    [Fact]
    public async Task CancelSubscriptionAsync_ReturnsCancelled()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("auth/tokens", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"t"}""");
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":222,"plan_id":111,"identifier":"cust-1","state":"cancelled"}""");
        });
        var provider = Create(handler);
        var sub = await provider.CancelSubscriptionAsync("222");
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
    }

    [Fact]
    public async Task PauseSubscriptionAsync_ReturnsPaused()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("auth/tokens", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"t"}""");
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":222,"plan_id":111,"identifier":"cust-1","state":"suspended"}""");
        });
        var provider = Create(handler);
        var sub = await provider.PauseSubscriptionAsync("222");
        Assert.Equal(SubscriptionStatus.Paused, sub.Status);
    }

    [Fact]
    public async Task CreatePlanAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Pro",
            Amount = 9m,
            Currency = "EGP",
            Interval = SubscriptionInterval.Monthly
        }));
    }
}
