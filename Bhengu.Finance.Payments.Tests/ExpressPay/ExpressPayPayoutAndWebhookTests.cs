// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.ExpressPay.Configuration;
using Bhengu.Finance.Payments.ExpressPay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.ExpressPay;

public class ExpressPayPayoutAndWebhookTests
{
    private static ExpressPayPaymentProvider Create(StubHttpMessageHandler handler, IBhenguDistributedCache? cache = null) =>
        new(new HttpClient(handler),
            Options.Create(new ExpressPayOptions
            {
                MerchantId = "demo",
                ApiKey = "demo-key",
                RedirectUrl = "https://merchant.example/return",
                PostUrl = "https://merchant.example/postback",
                Currency = "GHS"
            }),
            NullLogger<ExpressPayPaymentProvider>.Instance,
            cache);

    [Fact]
    public void Capabilities_AdvertisesPayoutTypedWebhooksIdempotency()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.Payout));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.TypedWebhooks));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.Idempotency));
    }

    [Fact]
    public async Task ProcessPayoutAsync_PostsToPayoutPhp_AndMapsCompleted()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("payout.php", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":2,"transaction-id":"po-1","message":"ok"}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "mtn:0244000000",
            Amount = 50m,
            Currency = "GHS",
            Description = "Vendor"
        });
        Assert.Equal("po-1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws_OnMalformedDestinationToken()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "nochannel",
            Amount = 1m,
            Currency = "GHS",
            Description = "bad"
        }));
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate limited"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "mtn:0244000000",
            Amount = 50m,
            Currency = "GHS",
            Description = "Vendor"
        }));
    }

    [Fact]
    public async Task ProcessPayoutAsync_HonoursIdempotencyKey_ViaCache()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":2,"transaction-id":"po-1"}
                """);
        });
        var cache = new InMemoryBhenguDistributedCache();
        var provider = Create(handler, cache);
        var request = new PayoutRequest
        {
            DestinationToken = "mtn:0244000000",
            Amount = 50m,
            Currency = "GHS",
            Description = "Vendor",
            IdempotencyKey = "po-idem"
        };

        var first = await provider.ProcessPayoutAsync(request);
        var second = await provider.ProcessPayoutAsync(request);
        Assert.Equal(1, calls);
        Assert.Equal(first.GatewayReference, second.GatewayReference);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeSucceededEvent_ForStatus1()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""{"token":"tok-a","status":1,"currency":"GHS","amount":"75.00"}""");
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(75m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeFailedEvent_ForStatus3()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("token=tok-b&status=3&currency=GHS&amount=10.00");
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsPayoutCompletedEvent_ForPayoutEventType()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""{"token":"po-1","status":2,"event":"payout.completed","currency":"GHS","amount":"50.00"}""");
        var typed = Assert.IsType<PayoutCompletedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutCompleted, typed.Category);
    }
}
