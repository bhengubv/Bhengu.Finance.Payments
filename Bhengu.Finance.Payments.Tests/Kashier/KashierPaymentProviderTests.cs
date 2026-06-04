// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Kashier.Configuration;
using Bhengu.Finance.Payments.Kashier.Internals;
using Bhengu.Finance.Payments.Kashier.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Kashier;

public class KashierPaymentProviderTests
{
    private static KashierPaymentProvider Create(StubHttpMessageHandler handler, KashierOptions? opts = null)
    {
        opts ??= new KashierOptions
        {
            ApiKey = "api_test_key",
            MerchantId = "MID_1",
            SecretKey = "secret_kashier",
            WebhookSecret = "webhook_secret_kashier",
            Currency = "EGP",
            Mode = "test",
            UseSandbox = true
        };
        var http = new HttpClient(handler);
        return new KashierPaymentProvider(http, Options.Create(opts), NullLogger<KashierPaymentProvider>.Instance,
            new KashierIdempotencyCache(new InMemoryBhenguDistributedCache()));
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "tok_card_kashier",
        Amount = 200m,
        Currency = "EGP",
        Description = "Kashier test"
    };

    [Fact]
    public void Constructor_Throws_WhenApiKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new KashierPaymentProvider(http, Options.Create(new KashierOptions { MerchantId = "x" }), NullLogger<KashierPaymentProvider>.Instance,
                new KashierIdempotencyCache(new InMemoryBhenguDistributedCache())));
    }

    [Fact]
    public void Constructor_Throws_WhenMerchantIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new KashierPaymentProvider(http, Options.Create(new KashierOptions { ApiKey = "k" }), NullLogger<KashierPaymentProvider>.Instance,
                new KashierIdempotencyCache(new InMemoryBhenguDistributedCache())));
    }

    [Fact]
    public void ProviderName_IsKashier()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("kashier", provider.ProviderName);
    }

    [Fact]
    public void Capabilities_Include3DSAndTokenisationAndTypedWebhooks()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.ThreeDSecure));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Tokenisation));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.TypedWebhooks));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Idempotency));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.PartialRefund));
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsCompletedResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("orders/", req.RequestUri!.PathAndQuery);
            Assert.Contains("/payments", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"SUCCESS","response":{"transactionId":"TX_KASH_1","orderId":"ORD_1","status":"SUCCESS","amount":"200.00","currency":"EGP"}}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());
        Assert.Equal("TX_KASH_1", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
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
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.InternalServerError, "boom"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("network"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsRefundedResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("payments/refund", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"SUCCESS","response":{"transactionId":"TX_KASH_1","status":"REFUNDED","amount":"100.00"}}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "TX_KASH_1",
            Amount = 100m,
            Reason = "Customer requested"
        });
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
        Assert.Equal(100m, refund.Amount);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsCompletedResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("payouts", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"SUCCESS","response":{"transactionId":"PO_KASH_1","status":"SUCCESS","amount":"500.00"}}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "vendor-1",
            Amount = 500m,
            Currency = "EGP",
            Description = "Vendor payout"
        });
        Assert.Equal("PO_KASH_1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
    }

    [Fact]
    public async Task ProcessPaymentAsync_DedupesOnSameIdempotencyKey()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                "{\"status\":\"SUCCESS\",\"response\":{\"transactionId\":\"TX_" + calls + "\",\"status\":\"SUCCESS\"}}");
        });
        var provider = Create(handler);
        var r1 = await provider.ProcessPaymentAsync(SamplePayment() with { IdempotencyKey = "shared" });
        var r2 = await provider.ProcessPaymentAsync(SamplePayment() with { IdempotencyKey = "shared" });
        Assert.Equal(r1.GatewayReference, r2.GatewayReference);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidHmacSha256()
    {
        const string secret = "webhook_secret_kashier";
        const string payload = """{"event":"PAY","data":{"transactionId":"TX_1","status":"SUCCESS"}}""";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(payload, sig));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("payload", "0000"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenAllSecretsMissing()
    {
        var opts = new KashierOptions { ApiKey = "k", MerchantId = "m", SecretKey = "", WebhookSecret = "" };
        var provider = Create(
            new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)),
            opts);
        Assert.False(provider.VerifyWebhookSignature("payload", "sig"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsTypedChargeSucceeded_OnPaySuccess()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event":"PAY","data":{"transactionId":"TX_PAY","status":"SUCCESS","amount":"100.00","currency":"EGP"}}
            """);
        Assert.NotNull(evt);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal("TX_PAY", typed.GatewayReference);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(100m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsTypedRefund_OnRefund()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event":"REFUND","data":{"transactionId":"TX_REF","status":"REFUNDED","amount":"50.00","currency":"EGP"}}
            """);
        Assert.NotNull(evt);
        var typed = Assert.IsType<RefundSucceededEvent>(evt);
        Assert.Equal(PaymentStatus.Refunded, typed.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsTypedChargeFailed_OnFailed()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event":"FAILED","data":{"transactionId":"TX_FAIL","status":"DECLINED","amount":"50.00","currency":"EGP","message":"insufficient_funds"}}
            """);
        Assert.NotNull(evt);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal("insufficient_funds", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("not json");
        Assert.Null(evt);
    }
}
