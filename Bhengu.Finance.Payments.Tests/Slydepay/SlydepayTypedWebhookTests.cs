// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Slydepay.Configuration;
using Bhengu.Finance.Payments.Slydepay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Slydepay;

public class SlydepayTypedWebhookTests
{
    private static SlydepayPaymentProvider Create(StubHttpMessageHandler handler, IBhenguDistributedCache? cache = null) =>
        new(new HttpClient(handler),
            Options.Create(new SlydepayOptions
            {
                EmailOrMobile = "merchant@example.com",
                MerchantKey = "mk-1",
                Currency = "GHS"
            }),
            NullLogger<SlydepayPaymentProvider>.Instance,
            cache);

    [Fact]
    public void Capabilities_AdvertisesTypedWebhooks_Idempotency()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.TypedWebhooks));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.Idempotency));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeSucceededEvent_ForConfirmed()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"payToken":"pt-1","orderCode":"ord-1","transactionStatus":"CONFIRMED","amount":42,"currency":"GHS"}
            """);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(42m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargePendingEvent_ForPending()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"payToken":"pt-2","orderCode":"ord-2","transactionStatus":"PENDING","amount":10,"currency":"GHS"}
            """);
        var typed = Assert.IsType<ChargePendingEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargePending, typed.Category);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeFailedEvent_ForFailed()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"payToken":"pt-3","transactionStatus":"FAILED","amount":5,"currency":"GHS"}
            """);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownStatus()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"payToken":"pt-4","transactionStatus":"WEIRD","amount":5}
            """);
        Assert.Null(evt);
    }

    [Fact]
    public async Task ProcessPaymentAsync_HonoursIdempotencyKey_ViaCache()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"success":true,"result":{"success":true,"payToken":"pt-1","checkOutUrl":"https://app.slydepay.com.gh/c/xyz"}}
                """);
        });
        var cache = new InMemoryBhenguDistributedCache();
        var provider = Create(handler, cache);
        var request = new PaymentRequest
        {
            PaymentMethodToken = "ord-1",
            Amount = 12.50m,
            Currency = "GHS",
            Description = "Test",
            IdempotencyKey = "idem-1"
        };

        var first = await provider.ProcessPaymentAsync(request);
        var second = await provider.ProcessPaymentAsync(request);
        Assert.Equal(1, calls);
        Assert.Equal(first.GatewayReference, second.GatewayReference);
    }
}
