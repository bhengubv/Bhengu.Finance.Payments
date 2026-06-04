// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.OrangeMoney.Configuration;
using Bhengu.Finance.Payments.OrangeMoney.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.OrangeMoney;

public class OrangeMoneyPayoutAndWebhookTests
{
    private static OrangeMoneyPaymentProvider Create(StubHttpMessageHandler handler, IBhenguDistributedCache? cache = null) =>
        new(new HttpClient(handler),
            Options.Create(new OrangeMoneyOptions
            {
                ConsumerKey = "ck",
                ConsumerSecret = "cs",
                MerchantKey = "mk-1",
                Country = "ci",
                ReturnUrl = "https://merchant.example/return",
                CancelUrl = "https://merchant.example/cancel",
                NotifUrl = "https://merchant.example/notif"
            }),
            NullLogger<OrangeMoneyPaymentProvider>.Instance,
            cache);

    private static StubHttpMessageHandler StubWithToken(Func<HttpRequestMessage, HttpResponseMessage> resourceHandler) =>
        new((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("oauth/v2/token"))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"access_token":"tok","token_type":"Bearer","expires_in":3600}""");
            return resourceHandler(req);
        });

    [Fact]
    public void Capabilities_AdvertisesPayoutTypedWebhooksIdempotency()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.Payout));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.TypedWebhooks));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.Idempotency));
    }

    [Fact]
    public async Task ProcessPayoutAsync_PostsToCashin_AndMapsCompleted()
    {
        var handler = StubWithToken(req =>
        {
            Assert.Contains("orange-money-b2c/ci/v1/cashin", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"SUCCESS","txnid":"txn-po-1","order_id":"ord-1"}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "225700000000",
            Amount = 1000m,
            Currency = "XOF",
            Description = "Vendor"
        });
        Assert.Equal("txn-po-1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws429AsProviderRateLimitException()
    {
        var handler = StubWithToken(_ => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "225700000000",
            Amount = 1000m,
            Currency = "XOF",
            Description = "Vendor"
        }));
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = StubWithToken(_ => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad msisdn"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "225700000000",
            Amount = 1000m,
            Currency = "XOF",
            Description = "Vendor"
        }));
    }

    [Fact]
    public async Task ProcessPayoutAsync_HonoursIdempotencyKey_ViaCache()
    {
        var calls = 0;
        var handler = StubWithToken(_ =>
        {
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"SUCCESS","txnid":"txn-po-1"}
                """);
        });
        var cache = new InMemoryBhenguDistributedCache();
        var provider = Create(handler, cache);
        var request = new PayoutRequest
        {
            DestinationToken = "225700000000",
            Amount = 1000m,
            Currency = "XOF",
            Description = "Vendor",
            IdempotencyKey = "po-idem-orange"
        };

        var first = await provider.ProcessPayoutAsync(request);
        var second = await provider.ProcessPayoutAsync(request);
        Assert.Equal(1, calls);
        Assert.Equal(first.GatewayReference, second.GatewayReference);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeSucceededEvent_ForSuccessStatus()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"status":"SUCCESS","pay_token":"pt-1","order_id":"ord-1","amount":"100","currency":"XOF"}
            """);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsPayoutCompletedEvent_ForCashinSuccess()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"status":"SUCCESS","pay_token":"po-1","notification_type":"cashin","amount":"1000","currency":"XOF"}
            """);
        var typed = Assert.IsType<PayoutCompletedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutCompleted, typed.Category);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeFailedEvent_ForFailedStatus()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"status":"FAILED","pay_token":"pt-2","amount":"50","currency":"XOF"}
            """);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
    }
}
