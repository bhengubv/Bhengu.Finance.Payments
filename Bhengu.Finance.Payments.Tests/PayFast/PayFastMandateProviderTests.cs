// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Mandate;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Bhengu.Finance.Payments.PayFast.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayFast;

public class PayFastMandateProviderTests
{
    private static PayFastMandateProvider Create(StubHttpMessageHandler handler, PayFastMandateAmountCache? cache = null)
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
        return new PayFastMandateProvider(
            http,
            Options.Create(opts),
            NullLogger<PayFastMandateProvider>.Instance,
            cache ?? new PayFastMandateAmountCache());
    }

    private static MandateRequest SampleMandate() => new()
    {
        CustomerId = "cust-1",
        BankAccountToken = string.Empty, // PayFast tokenisation works without an upfront account number
        AmountLimit = 500m,
        Currency = "ZAR",
        Description = "Recurring sub debit"
    };

    [Fact]
    public async Task CreateMandateAsync_ReturnsAuthorisationUrlWithSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        var mandate = await provider.CreateMandateAsync(SampleMandate());

        Assert.Equal(MandateStatus.Pending, mandate.Status);
        Assert.NotNull(mandate.AuthorisationUrl);
        Assert.StartsWith("https://sandbox.payfast.co.za/eng/process?", mandate.AuthorisationUrl);
        Assert.Contains("subscription_type=2", mandate.AuthorisationUrl);
        Assert.Contains("amount=0", mandate.AuthorisationUrl);
        Assert.Contains("signature=", mandate.AuthorisationUrl);
        Assert.Equal(500m, mandate.AmountLimit);
        Assert.Equal("ZAR", mandate.Currency);
    }

    [Fact]
    public async Task ChargeMandateAsync_PostsToAdhocEndpoint()
    {
        string? capturedPath = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            capturedPath = req.RequestUri!.PathAndQuery;
            Assert.Equal(HttpMethod.Post, req.Method);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code":200,"status":"success","data":{"message":true,"pf_payment_id":"PF-CHG-1","response":"APPROVED","response_reason":"OK"}}
                """);
        });

        var cache = new PayFastMandateAmountCache();
        cache.Set("tok-1", 500m, "ZAR");
        var provider = Create(handler, cache);

        var result = await provider.ChargeMandateAsync(new MandateChargeRequest
        {
            MandateReference = "tok-1",
            Amount = 100m,
            Currency = "ZAR",
            Description = "Monthly sub"
        });

        Assert.NotNull(capturedPath);
        Assert.Contains("subscriptions/tok-1/adhoc", capturedPath);
        Assert.Equal(PaymentStatus.Completed, result.Status);
        Assert.Equal("PF-CHG-1", result.GatewayReference);
        Assert.Equal(100m, result.Amount);
    }

    [Fact]
    public async Task GetMandateAsync_ReturnsActiveMapping()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains("/fetch", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code":200,"status":"success","data":{"token":"tok-2","status":1,"status_text":"Active","custom_str1":"cust-9"}}
                """);
        });

        var cache = new PayFastMandateAmountCache();
        cache.Set("tok-2", 250m, "ZAR");
        var provider = Create(handler, cache);

        var mandate = await provider.GetMandateAsync("tok-2");
        Assert.NotNull(mandate);
        Assert.Equal(MandateStatus.Active, mandate!.Status);
        Assert.Equal(250m, mandate.AmountLimit);
    }

    [Fact]
    public async Task CancelMandateAsync_ReturnsCancelledStatus()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Put)
            {
                Assert.Contains("/cancel", req.RequestUri!.PathAndQuery);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        });

        var provider = Create(handler);
        var result = await provider.CancelMandateAsync("tok-1");
        Assert.Equal(MandateStatus.Cancelled, result.Status);
        Assert.NotNull(result.CancelledAt);
    }

    [Fact]
    public async Task ChargeMandateAsync_Throws4xxAsPaymentDeclined()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "card declined"));
        var cache = new PayFastMandateAmountCache();
        cache.Set("tok-bad", 500m, "ZAR");
        var provider = Create(handler, cache);

        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ChargeMandateAsync(new MandateChargeRequest
        {
            MandateReference = "tok-bad",
            Amount = 100m,
            Currency = "ZAR",
            Description = "test"
        }));
        Assert.Equal("400", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task ChargeMandateAsync_Throws5xxAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
        var cache = new PayFastMandateAmountCache();
        cache.Set("tok-x", 500m, "ZAR");
        var provider = Create(handler, cache);

        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ChargeMandateAsync(new MandateChargeRequest
        {
            MandateReference = "tok-x",
            Amount = 50m,
            Currency = "ZAR",
            Description = "fail"
        }));
    }

    [Fact]
    public async Task ChargeMandateAsync_WithoutAmountCachedSkipsLimitCheck()
    {
        // No cache entry — the provider should NOT reject up-front; it sends and lets PayFast decide.
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"code":200,"status":"success","data":{"message":true,"pf_payment_id":"PF-Z","response":"APPROVED"}}
            """));
        var provider = Create(handler);
        var result = await provider.ChargeMandateAsync(new MandateChargeRequest
        {
            MandateReference = "no-cache",
            Amount = 999m,
            Currency = "ZAR",
            Description = "anything"
        });
        Assert.Equal(PaymentStatus.Completed, result.Status);
    }

    [Fact]
    public async Task ChargeMandateAsync_ThrowsWhenAmountExceedsLimit()
    {
        var cache = new PayFastMandateAmountCache();
        cache.Set("tok-cap", 100m, "ZAR");
        // Handler isn't reached — the cache check rejects up-front.
        var provider = Create(new StubHttpMessageHandler((_, _) =>
        {
            Assert.Fail("HTTP should not be called when local limit is exceeded.");
            return new HttpResponseMessage(HttpStatusCode.OK);
        }), cache);

        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.ChargeMandateAsync(new MandateChargeRequest
        {
            MandateReference = "tok-cap",
            Amount = 500m,
            Currency = "ZAR",
            Description = "over"
        }));
        Assert.Equal("amount_over_limit", ex.ProviderErrorCode);
    }
}
