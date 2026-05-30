// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Bhengu.Finance.Payments.Razorpay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Razorpay;

public class RazorpayPaymentProviderTests
{
    private static RazorpayPaymentProvider Create(StubHttpMessageHandler handler, RazorpayOptions? opts = null)
    {
        opts ??= new RazorpayOptions
        {
            KeyId = "rzp_test_xx",
            KeySecret = "secret_xx",
            WebhookSecret = "webhook-test-secret",
            RazorpayXAccountNumber = "2323230099089860"
        };
        var http = new HttpClient(handler);
        return new RazorpayPaymentProvider(http, Options.Create(opts), NullLogger<RazorpayPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "pay_abc123",
        Amount = 100m,
        Currency = "INR",
        Description = "Razorpay test"
    };

    [Fact]
    public void Constructor_Throws_WhenKeyIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new RazorpayPaymentProvider(http, Options.Create(new RazorpayOptions { KeySecret = "x" }),
                NullLogger<RazorpayPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenKeySecretMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new RazorpayPaymentProvider(http, Options.Create(new RazorpayOptions { KeyId = "x" }),
                NullLogger<RazorpayPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsRazorpay()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("razorpay", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_CapturesPayment_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("v1/payments/pay_abc123/capture", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"pay_abc123","entity":"payment","amount":10000,"currency":"INR","status":"captured","order_id":"order_x","method":"card"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("pay_abc123", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Equal(100m, response.Amount);
        Assert.Equal("INR", response.Currency);
    }

    [Fact]
    public async Task ProcessPaymentAsync_OrderFlow_CreatesOrder_AndReturnsOrderId()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v1/orders", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"order_QYxRyzUaAFA13o","entity":"order","amount":10000,"currency":"INR","status":"created","receipt":"rcpt_1"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "unused-for-orders-flow",
            Amount = 100m,
            Currency = "INR",
            Description = "Razorpay order",
            Metadata = new Dictionary<string, string> { ["flow"] = "order", ["receipt"] = "rcpt_1" }
        });

        Assert.Equal("order_QYxRyzUaAFA13o", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Contains("order_QYxRyzUaAFA13o", response.Message);
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
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "invalid card"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS fail"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsRefundedResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v1/payments/pay_abc123/refund", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"rfnd_1","entity":"refund","amount":5000,"currency":"INR","payment_id":"pay_abc123","status":"processed"}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "pay_abc123",
            Amount = 50m,
            Reason = "Customer requested"
        });

        Assert.Equal("rfnd_1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, refund.Status); // "processed" maps to Completed
        Assert.Equal(50m, refund.Amount);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v1/payouts", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"pout_1","entity":"payout","amount":50000,"currency":"INR","status":"processed","mode":"IMPS"}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "fa_test123",
            Amount = 500m,
            Currency = "INR",
            Description = "Vendor payout"
        });

        Assert.Equal("pout_1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
        Assert.Equal(500m, payout.Amount);
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws_WhenRazorpayXAccountNumberMissing()
    {
        var provider = Create(
            new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")),
            new RazorpayOptions { KeyId = "x", KeySecret = "y", RazorpayXAccountNumber = "" });
        await Assert.ThrowsAsync<ProviderConfigurationException>(() => provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "fa_x",
            Amount = 10m,
            Currency = "INR",
            Description = "x"
        }));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenWebhookSecretMissing()
    {
        var provider = Create(
            new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)),
            new RazorpayOptions { KeyId = "x", KeySecret = "y", WebhookSecret = "" });
        Assert.False(provider.VerifyWebhookSignature("payload", "sig"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string secret = "webhook-test-secret";
        const string payload = """{"event":"payment.captured","payload":{"payment":{"entity":{"id":"pay_1"}}}}""";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var validSig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(payload, validSig));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("anything", "deadbeef"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForPaymentCaptured()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event":"payment.captured","payload":{"payment":{"entity":{"id":"pay_99","status":"captured"}}}}
            """);
        Assert.NotNull(evt);
        Assert.Equal("pay_99", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("payment.captured", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownEventType()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event":"some.unknown.event","payload":{"payment":{"entity":{"id":"x"}}}}
            """);
        Assert.Null(evt);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
