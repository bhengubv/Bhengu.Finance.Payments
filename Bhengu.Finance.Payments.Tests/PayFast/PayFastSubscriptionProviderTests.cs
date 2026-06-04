// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Bhengu.Finance.Payments.PayFast.Internals;
using Bhengu.Finance.Payments.PayFast.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayFast;

public class PayFastSubscriptionProviderTests
{
    private static PayFastSubscriptionProvider Create(StubHttpMessageHandler handler, PayFastPlanCache? cache = null)
    {
        var opts = new PayFastOptions
        {
            MerchantId = "10000100",
            MerchantKey = "46f0cd694581a",
            Passphrase = "jt7NOE43FZPn",
            UseSandbox = true,
            ReturnUrl = "https://example.com/return",
            CancelUrl = "https://example.com/cancel",
            NotifyUrl = "https://example.com/notify"
        };
        var http = new HttpClient(handler);
        return new PayFastSubscriptionProvider(
            http,
            Options.Create(opts),
            NullLogger<PayFastSubscriptionProvider>.Instance,
            cache ?? new PayFastPlanCache());
    }

    [Fact]
    public async Task CreatePlanAsync_StoresPlanAndReturnsReference()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        var plan = await provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Pro Monthly",
            Amount = 99m,
            Currency = "ZAR",
            Interval = SubscriptionInterval.Monthly,
            TotalCycles = 12,
            Description = "Best plan"
        });

        Assert.StartsWith("pfplan-", plan.Reference);
        Assert.Equal("Pro Monthly", plan.Name);
        Assert.Equal(99m, plan.Amount);
        Assert.Equal(SubscriptionInterval.Monthly, plan.Interval);
        Assert.Equal(12, plan.TotalCycles);

        var fetched = await provider.GetPlanAsync(plan.Reference);
        Assert.NotNull(fetched);
        Assert.Equal(plan.Reference, fetched!.Reference);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_ReturnsAuthorisationUrlWithSignature()
    {
        var cache = new PayFastPlanCache();
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")), cache);

        var plan = await provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Monthly",
            Amount = 50m,
            Currency = "ZAR",
            Interval = SubscriptionInterval.Monthly,
            TotalCycles = 0
        });

        var sub = await provider.CreateSubscriptionAsync(new SubscriptionRequest
        {
            PlanReference = plan.Reference,
            CustomerId = "cust-1",
            PaymentMethodToken = "ignored-on-redirect"
        });

        Assert.Equal(plan.Reference, sub.PlanReference);
        Assert.Equal("cust-1", sub.CustomerId);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);

        var url = sub.AuthorisationUrl;
        Assert.NotNull(url);
        Assert.StartsWith("https://sandbox.payfast.co.za/eng/process?", url);
        Assert.Contains("subscription_type=1", url);
        Assert.Contains("frequency=3", url);
        Assert.Contains("signature=", url);
        Assert.Contains("merchant_id=10000100", url);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_ThrowsWhenPlanMissing()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            provider.CreateSubscriptionAsync(new SubscriptionRequest
            {
                PlanReference = "unknown",
                CustomerId = "c",
                PaymentMethodToken = "t"
            }));
        Assert.Equal("plan_not_found", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_ReturnsCancelledOnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Put)
            {
                Assert.Contains("/cancel", req.RequestUri!.PathAndQuery);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code":200,"status":"success","data":{"status":2,"status_text":"Cancelled"}}
                """);
        });

        var provider = Create(handler);
        var result = await provider.CancelSubscriptionAsync("token-abc");
        Assert.Equal(SubscriptionStatus.Cancelled, result.Status);
        Assert.NotNull(result.CancelledAt);
        Assert.Null(result.NextBillingAt);
    }

    [Fact]
    public async Task PauseAndResumeSubscriptionAsync_Succeed()
    {
        var paths = new List<string>();
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            paths.Add(req.RequestUri!.PathAndQuery);
            if (req.Method == HttpMethod.Put)
                return new HttpResponseMessage(HttpStatusCode.OK);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code":200,"status":"success","data":{"status":1}}
                """);
        });
        var provider = Create(handler);

        var paused = await provider.PauseSubscriptionAsync("token-1");
        Assert.Equal(SubscriptionStatus.Paused, paused.Status);
        Assert.Contains(paths, p => p.Contains("/pause"));

        var resumed = await provider.ResumeSubscriptionAsync("token-1");
        Assert.Equal(SubscriptionStatus.Active, resumed.Status);
        Assert.Contains(paths, p => p.Contains("/unpause"));
    }

    [Fact]
    public async Task GetSubscriptionAsync_MapsActiveStatus()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains("/fetch", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code":200,"status":"success","data":{"token":"tok-1","status":1,"status_text":"Active","cycles_complete":3,"custom_str1":"cust-x","custom_str2":"plan-y"}}
                """);
        });
        var provider = Create(handler);

        var sub = await provider.GetSubscriptionAsync("tok-1");
        Assert.NotNull(sub);
        Assert.Equal(SubscriptionStatus.Active, sub!.Status);
        Assert.Equal(3, sub.CyclesCompleted);
        Assert.Equal("plan-y", sub.PlanReference);
        Assert.Equal("cust-x", sub.CustomerId);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_IsIdempotent_WhenAlreadyCancelled()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            calls++;
            if (req.Method == HttpMethod.Put)
            {
                // PayFast returns 400 with "already cancelled" — provider should swallow it.
                return StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "subscription already cancelled");
            }
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code":200,"status":"success","data":{"status":2,"status_text":"Cancelled"}}
                """);
        });
        var provider = Create(handler);
        var result = await provider.CancelSubscriptionAsync("tok-already");
        Assert.Equal(SubscriptionStatus.Cancelled, result.Status);
        Assert.True(calls >= 1);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_Throws4xxAsPaymentDeclined()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Put)
                return StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "invalid token");
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code":200,"status":"success","data":{"status":1}}
                """);
        });
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.CancelSubscriptionAsync("tok-bad"));
    }

    [Fact]
    public async Task CancelSubscriptionAsync_Throws5xxAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Put)
                return StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down");
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code":200,"status":"success","data":{"status":1}}
                """);
        });
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.CancelSubscriptionAsync("tok-x"));
    }
}
