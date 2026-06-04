// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.JamboPay.Configuration;
using Bhengu.Finance.Payments.JamboPay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.JamboPay;

public class JamboPayTypedWebhookTests
{
    private static JamboPayPaymentProvider Create(StubHttpMessageHandler handler, IBhenguDistributedCache? cache = null) =>
        new(new HttpClient(handler),
            Options.Create(new JamboPayOptions
            {
                ApiKey = "ak",
                ClientId = "ci",
                ClientSecret = "cs",
                MerchantCode = "MERCH-1",
                WebhookSecret = "whsec",
                CallbackUrl = "https://merchant.example/cb",
                Currency = "KES"
            }),
            NullLogger<JamboPayPaymentProvider>.Instance,
            cache);

    [Fact]
    public void Capabilities_AdvertisesTypedWebhooksIdempotency()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.TypedWebhooks));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.Idempotency));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeSucceededEvent_ForPaymentCompleted()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event":"payment.completed","transaction_ref":"tx-1","status":"successful","amount":100,"currency":"KES"}
            """);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(100m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeFailedEvent_ForPaymentFailed()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event":"payment.failed","transaction_ref":"tx-2","status":"failed","amount":50,"currency":"KES","message":"declined"}
            """);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal("declined", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsRefundSucceededEvent_ForRefundCompleted()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event":"refund.completed","transaction_ref":"tx-1","refund_id":"rf-1","status":"refunded","amount":50,"currency":"KES"}
            """);
        var typed = Assert.IsType<RefundSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundSucceeded, typed.Category);
        Assert.Equal("rf-1", typed.RefundReference);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsPayoutCompletedEvent_ForPayoutCompleted()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event":"payout.completed","transaction_ref":"po-1","status":"successful","amount":500,"currency":"KES"}
            """);
        var typed = Assert.IsType<PayoutCompletedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutCompleted, typed.Category);
    }

    [Fact]
    public async Task ProcessPaymentAsync_HonoursIdempotencyKey_ViaCache()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("oauth/token"))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"access_token":"tok","token_type":"Bearer","expires_in":3600}""");
            calls++;
            Assert.True(req.Headers.Contains("Idempotency-Key"));
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"transaction_ref":"tx-1","status":"pending","checkout_url":"https://jambopay.com/c/x","message":"ok"}
                """);
        });
        var cache = new InMemoryBhenguDistributedCache();
        var provider = Create(handler, cache);
        var request = new PaymentRequest
        {
            PaymentMethodToken = "tx-1",
            Amount = 100m,
            Currency = "KES",
            Description = "test",
            IdempotencyKey = "idem-1"
        };

        var first = await provider.ProcessPaymentAsync(request);
        var second = await provider.ProcessPaymentAsync(request);
        Assert.Equal(1, calls);
        Assert.Equal(first.GatewayReference, second.GatewayReference);
    }

    [Fact]
    public async Task ProcessPayoutAsync_HonoursIdempotencyKey_ViaCache()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("oauth/token"))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"access_token":"tok","token_type":"Bearer","expires_in":3600}""");
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"payout_id":"po-1","status":"successful"}
                """);
        });
        var cache = new InMemoryBhenguDistributedCache();
        var provider = Create(handler, cache);
        var request = new PayoutRequest
        {
            DestinationToken = "msisdn:254700000000",
            Amount = 500m,
            Currency = "KES",
            Description = "salary",
            IdempotencyKey = "po-idem"
        };

        var first = await provider.ProcessPayoutAsync(request);
        var second = await provider.ProcessPayoutAsync(request);
        Assert.Equal(1, calls);
        Assert.Equal(first.GatewayReference, second.GatewayReference);
    }
}
