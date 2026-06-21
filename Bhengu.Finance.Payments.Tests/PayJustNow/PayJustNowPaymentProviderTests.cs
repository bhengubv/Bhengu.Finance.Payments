// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Text;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.PayJustNow.Configuration;
using Bhengu.Finance.Payments.PayJustNow.Internals;
using Bhengu.Finance.Payments.PayJustNow.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayJustNow;

/// <summary>
/// Tests assert the PayJustNow merchant-API wire format verified against PayJustNow's public production
/// WooCommerce gateway (payjustnow.class.php v2.7.9) + public API README: HTTP Basic auth, paths
/// <c>/api/v1/merchant/checkout</c> and <c>/api/v1/merchant/refund</c>, nested checkout body returning
/// <c>{data:{token,redirect_to}}</c>, and a full/partial refund returning <c>{status:"REFUNDED"...}</c>.
/// </summary>
public class PayJustNowPaymentProviderTests
{
    private static PayJustNowPaymentProvider Create(StubHttpMessageHandler handler, PayJustNowOptions? opts = null)
    {
        opts ??= new PayJustNowOptions
        {
            ApiKey = "secret",
            MerchantId = "1",
            UseSandbox = true
        };
        var http = new HttpClient(handler);
        return new PayJustNowPaymentProvider(http, Options.Create(opts), NullLogger<PayJustNowPaymentProvider>.Instance,
            new PayJustNowIdempotencyCache(new InMemoryBhenguDistributedCache()));
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "pjn-token",
        Amount = 300m,
        Currency = "ZAR",
        Description = "PJN test"
    };

    [Fact]
    public void Constructor_Throws_WhenApiKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new PayJustNowPaymentProvider(http, Options.Create(new PayJustNowOptions { MerchantId = "x" }), NullLogger<PayJustNowPaymentProvider>.Instance,
                new PayJustNowIdempotencyCache(new InMemoryBhenguDistributedCache())));
    }

    [Fact]
    public void Constructor_Throws_WhenMerchantIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new PayJustNowPaymentProvider(http, Options.Create(new PayJustNowOptions { ApiKey = "x" }), NullLogger<PayJustNowPaymentProvider>.Instance,
                new PayJustNowIdempotencyCache(new InMemoryBhenguDistributedCache())));
    }

    [Fact]
    public void Capabilities_AreChargeRefundRedirectOnly_NoSubscriptionsOrMandatesOrWebhooks()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Charge));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Refund));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.PartialRefund));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.RedirectFlow));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Idempotency));
        // Not supported by PayJustNow's public API — must NOT be advertised.
        Assert.False(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Subscriptions));
        Assert.False(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Mandates));
        Assert.False(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Webhook));
        Assert.False(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.TypedWebhooks));
        Assert.False(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Payout));
    }

    [Fact]
    public async Task ProcessPaymentAsync_UsesBasicAuth_AndCheckoutPath()
    {
        string? authScheme = null, authParam = null, path = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            authScheme = req.Headers.Authorization?.Scheme;
            authParam = req.Headers.Authorization?.Parameter;
            path = req.RequestUri!.AbsolutePath;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":{"token":"pjn_tok_1","expires_at":"2026-07-04T00:00:00Z","redirect_to":"https://sandbox.payjustnow.com/checkout/pjn_tok_1"}}
                """);
        });
        var provider = Create(handler);
        await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("Basic", authScheme);
        // base64("1:secret") — Merchant ID is the Basic user, API key the password.
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("1:secret")), authParam);
        Assert.Equal("/api/v1/merchant/checkout", path);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsPendingWithRedirect_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("checkout", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":{"token":"pjn_tok_1","expires_at":"2026-07-04T00:00:00Z","redirect_to":"https://sandbox.payjustnow.com/checkout/pjn_tok_1"}}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("pjn_tok_1", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal("https://sandbox.payjustnow.com/checkout/pjn_tok_1", response.RedirectUrl);
        Assert.Equal("BNPL checkout created", response.Message);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws_WhenResponseHasMessageButNoToken()
    {
        // PayJustNow returns a top-level 'message' on a rejected/unauthorised checkout (no data/token).
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"message":"Invalid merchant credentials"}"""));
        var provider = Create(handler);
        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
        Assert.Contains("Invalid merchant credentials", ex.ProviderErrorMessage);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate limited"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "declined"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("network down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_DedupesOnSameIdempotencyKey()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            var json = "{\"data\":{\"token\":\"pjn_" + calls +
                "\",\"redirect_to\":\"https://sandbox.payjustnow.com/checkout/pjn_" + calls + "\"}}";
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, json);
        });
        var provider = Create(handler);
        var r1 = await provider.ProcessPaymentAsync(SamplePayment() with { IdempotencyKey = "abc" });
        var r2 = await provider.ProcessPaymentAsync(SamplePayment() with { IdempotencyKey = "abc" });
        Assert.Equal(r1.GatewayReference, r2.GatewayReference);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ProcessRefundAsync_FullRefund_ReturnsRefunded()
    {
        string? path = null, bodyJson = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            path = req.RequestUri!.AbsolutePath;
            bodyJson = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"REFUNDED","reason":"Order successfully refunded","refunded_at":"2026-02-08T09:03:56.292949Z","amount_refunded":30000}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "pjn_tok_1",
            Amount = 300m,
            Reason = "Customer requested"
        });

        Assert.Equal("/api/v1/merchant/refund", path);
        Assert.Contains("\"type\":\"full\"", bodyJson);
        Assert.Contains("\"token\":\"pjn_tok_1\"", bodyJson);
        Assert.Equal("pjn_tok_1", refund.GatewayReference);
        Assert.Equal(300m, refund.Amount);
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
    }

    [Fact]
    public async Task ProcessRefundAsync_PartialRefund_SendsTypePartial()
    {
        string? bodyJson = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            bodyJson = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"status":"REFUNDED","reason":"Refunded","refunded_at":"2026-02-08T09:03:56Z","amount_refunded":10000}""");
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "pjn_tok_1",
            Amount = 100m,
            OriginalAmount = 300m, // makes IsPartial true
            Reason = "Partial"
        });

        Assert.Contains("\"type\":\"partial\"", bodyJson);
        Assert.Equal(100m, refund.Amount);
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
    }

    [Fact]
    public async Task ProcessRefundAsync_Throws_OnFailedStatus()
    {
        // PayJustNow refund failure: {"status":"FAILED","reason":{"errors":{"code":400,"message":"..."}}}
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"status":"FAILED","reason":{"errors":{"code":400,"message":"Amount is greater than available amount refundable."}}}
            """));
        var provider = Create(handler);
        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "pjn_tok_1",
            Amount = 999m,
            Reason = "too much"
        }));
        Assert.Contains("greater than available", ex.ProviderErrorMessage);
    }

    [Fact]
    public void VerifyWebhookSignature_AlwaysReturnsFalse_NoSignedWebhook()
    {
        // PayJustNow has no cryptographically-signed server webhook — only a browser redirect callback.
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("payload", "sig"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_NoMachineReadableWebhook()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("""{"status":"SUCCESS","token":"pjn_tok_1"}"""));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
