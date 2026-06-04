// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Pesapal.Configuration;
using Bhengu.Finance.Payments.Pesapal.Internals;
using Bhengu.Finance.Payments.Pesapal.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Pesapal;

public class PesapalTypedWebhookTests
{
    private static PesapalPaymentProvider Create(StubHttpMessageHandler handler, IBhenguDistributedCache? cache = null) =>
        new(new HttpClient(handler),
            Options.Create(new PesapalOptions
            {
                ConsumerKey = "ck",
                ConsumerSecret = "cs",
                IpnId = "ipn-1",
                CallbackUrl = "https://merchant.example/cb",
                Currency = "KES"
            }),
            NullLogger<PesapalPaymentProvider>.Instance,
            cache,
            new PesapalTokenCache());

    [Fact]
    public void Capabilities_AdvertisesSubscriptionsSettlementsTypedWebhooks()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.Subscriptions));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.Settlement));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.TypedWebhooks));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.Idempotency));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargePendingEvent_ForIpnChangeType()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"OrderTrackingId":"otk-1","OrderMerchantReference":"sub-1","OrderNotificationType":"IPNCHANGE"}
            """);
        var typed = Assert.IsType<ChargePendingEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargePending, typed.Category);
        Assert.Equal("otk-1", typed.GatewayReference);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForMissingTrackingId()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""{"OrderMerchantReference":"sub-1"}""");
        Assert.Null(evt);
    }

    [Fact]
    public async Task ProcessPaymentAsync_HonoursIdempotencyKey_ViaCache()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("Auth/RequestToken"))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"tok","expiryDate":"2026-06-04T00:00:00Z"}""");
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"order_tracking_id":"otk-1","merchant_reference":"m-ref","redirect_url":"https://pay.pesapal.com/iframe","status":"200"}
                """);
        });
        var cache = new InMemoryBhenguDistributedCache();
        var provider = Create(handler, cache);

        var request = new PaymentRequest
        {
            PaymentMethodToken = "ord-1",
            Amount = 500m,
            Currency = "KES",
            Description = "test",
            IdempotencyKey = "idem-1"
        };

        var first = await provider.ProcessPaymentAsync(request);
        var second = await provider.ProcessPaymentAsync(request);
        Assert.Equal(1, calls);
        Assert.Equal(first.GatewayReference, second.GatewayReference);
    }
}
