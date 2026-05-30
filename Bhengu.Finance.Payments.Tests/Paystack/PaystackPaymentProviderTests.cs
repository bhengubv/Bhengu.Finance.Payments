// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paystack;

public class PaystackPaymentProviderTests
{
    private static PaystackPaymentProvider Create(StubHttpMessageHandler handler, PaystackOptions? opts = null)
    {
        opts ??= new PaystackOptions
        {
            SecretKey = "sk_test_xx",
            WebhookSecret = "webhook-test-secret",
            DefaultEmail = "buyer@example.com"
        };
        var http = new HttpClient(handler);
        return new PaystackPaymentProvider(http, Options.Create(opts), NullLogger<PaystackPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "AUTH_abc123",
        Amount = 100m,
        Currency = "NGN",
        Description = "Paystack test"
    };

    [Fact]
    public void Constructor_Throws_WhenSecretKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new PaystackPaymentProvider(http, Options.Create(new PaystackOptions()), NullLogger<PaystackPaymentProvider>.Instance));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws_WhenEmailMissing()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler, new PaystackOptions { SecretKey = "sk_x", DefaultEmail = null! });
        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            provider.ProcessPaymentAsync(new PaymentRequest
            {
                PaymentMethodToken = "AUTH_x",
                Amount = 10m,
                Currency = "NGN",
                Description = "no-email"
            }));
        Assert.Equal("missing_email", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsCompletedResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("transaction/charge_authorization", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"message":"Charge attempted","data":{"id":1234,"reference":"ref_paystack_1","status":"success","amount":10000,"currency":"NGN"}}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("ref_paystack_1", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Equal("Charge attempted", response.Message);
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
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "invalid auth"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.InternalServerError, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("transfer", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"message":"Transfer started","data":{"transfer_code":"TR_xxx","reference":"transfer-1","status":"success","amount":50000,"reason":"payout"}}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "RCP_test",
            Amount = 500m,
            Currency = "NGN",
            Description = "Vendor payout"
        });

        Assert.Equal("transfer-1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
        Assert.Equal(500m, payout.Amount);
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("refund", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"message":"Refund queued","data":{"transaction_id":1234,"refund_reference":"rf_paystack_1","amount":5000,"status":"processed"}}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "ref_paystack_1",
            Amount = 50m,
            Reason = "Customer requested"
        });

        Assert.Equal("rf_paystack_1", refund.GatewayReference);
    }

    [Fact]
    public void VerifyWebhookSignature_FallsBackToSecretKey_WhenWebhookSecretEmpty()
    {
        // Paystack permits using the merchant SecretKey for webhook verification when no
        // dedicated WebhookSecret is configured. Verify the fallback path works.
        const string secret = "sk_test_fallback_key";
        const string payload = """{"event":"charge.success","data":{"reference":"ref_1"}}""";
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
        var validSig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        var provider = Create(
            new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)),
            new PaystackOptions { SecretKey = secret, WebhookSecret = "", DefaultEmail = "buyer@example.com" });

        Assert.True(provider.VerifyWebhookSignature(payload, validSig));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string secret = "webhook-test-secret";
        const string payload = """{"event":"charge.success","data":{"reference":"ref_1"}}""";
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
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
    public async Task ParseWebhookAsync_ReturnsEvent_ForChargeSuccess()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event":"charge.success","data":{"reference":"ref_psk_99","status":"success"}}
            """);
        Assert.NotNull(evt);
        Assert.Equal("ref_psk_99", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("charge.success", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
