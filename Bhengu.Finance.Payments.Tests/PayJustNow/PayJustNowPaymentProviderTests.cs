// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.PayJustNow.Configuration;
using Bhengu.Finance.Payments.PayJustNow.Internals;
using Bhengu.Finance.Payments.PayJustNow.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayJustNow;

public class PayJustNowPaymentProviderTests
{
    private static PayJustNowPaymentProvider Create(StubHttpMessageHandler handler, PayJustNowOptions? opts = null)
    {
        opts ??= new PayJustNowOptions
        {
            ApiKey = "key",
            SecretKey = "webhook-secret",
            MerchantId = "merchant-1",
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
    public void Capabilities_IncludeSubscriptionsAndMandatesAndTypedWebhooks()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Subscriptions));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Mandates));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.TypedWebhooks));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Idempotency));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.PartialRefund));
        Assert.False(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Payout));
        Assert.False(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.ThreeDSecure));
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("orders", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"order_id":"pjn_order_1","status":"approved","checkout_url":"https://sandbox.payjustnow.com/checkout/pjn_order_1","amount":30000,"currency":"ZAR"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("pjn_order_1", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Equal("https://sandbox.payjustnow.com/checkout/pjn_order_1", response.RedirectUrl);
        Assert.Equal("BNPL plan created", response.Message);
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
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                $$"""{"order_id":"pjn_{{calls}}","status":"approved"}""");
        });
        var provider = Create(handler);
        var r1 = await provider.ProcessPaymentAsync(SamplePayment() with { IdempotencyKey = "abc" });
        var r2 = await provider.ProcessPaymentAsync(SamplePayment() with { IdempotencyKey = "abc" });
        Assert.Equal(r1.GatewayReference, r2.GatewayReference);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("refunds", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"refund_id":"pjn_refund_1","status":"refunded"}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "pjn_order_1",
            Amount = 100m,
            Reason = "Customer requested"
        });
        Assert.Equal("pjn_refund_1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenSecretMissing()
    {
        var provider = Create(
            new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)),
            new PayJustNowOptions { ApiKey = "k", MerchantId = "m", SecretKey = "" });
        Assert.False(provider.VerifyWebhookSignature("payload", "sig"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string secret = "webhook-secret";
        const string payload = """{"event_type":"order.approved","order_id":"pjn_1"}""";
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
    public async Task ParseWebhookAsync_ReturnsTypedChargeSucceeded_ForOrderApproved()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event_type":"order.approved","order_id":"pjn_99","amount":15000,"currency":"ZAR"}
            """);
        Assert.NotNull(evt);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal("pjn_99", typed.GatewayReference);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(150m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsTypedSubscriptionRenewed_OnInstalmentPaid()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event_type":"instalment.paid","order_id":"pjn_99","amount":5000,"currency":"ZAR","next_instalment_at":"2026-07-04T00:00:00Z"}
            """);
        Assert.NotNull(evt);
        var typed = Assert.IsType<SubscriptionRenewedEvent>(evt);
        Assert.Equal(50m, typed.Amount);
        Assert.NotNull(typed.NextBillingAt);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsTypedMandateActivated()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event_type":"mandate.activated","order_id":"pjn_mandate_5","amount":15000,"currency":"ZAR"}
            """);
        Assert.NotNull(evt);
        var typed = Assert.IsType<MandateActivatedEvent>(evt);
        Assert.Equal(WebhookEventCategory.MandateActivated, typed.Category);
        Assert.Equal("pjn_mandate_5", typed.MandateReference);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownEventType()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("""
            {"event_type":"some.unknown","order_id":"pjn_99"}
            """));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
