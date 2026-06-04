// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.IPay.Configuration;
using Bhengu.Finance.Payments.IPay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.IPay;

public class IPayPayoutAndWebhookTests
{
    private static IPayPaymentProvider Create(StubHttpMessageHandler handler, IBhenguDistributedCache? cache = null) =>
        new(new HttpClient(handler),
            Options.Create(new IPayOptions
            {
                VendorId = "demo",
                HashKey = "demoCHANGED",
                Live = "1",
                Currency = "KES",
                CallbackUrl = "https://merchant.example/ipay/callback"
            }),
            NullLogger<IPayPaymentProvider>.Instance,
            cache);

    [Fact]
    public void Capabilities_AdvertisesPayoutSettlementTypedWebhooksIdempotency()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.Payout));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.Settlement));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.TypedWebhooks));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.Idempotency));
    }

    [Fact]
    public async Task ProcessPayoutAsync_PostsToMpesaB2c_AndMapsCompleted()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("payments/v3/mpesab2c", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","transaction_id":"po-1","message":"ok"}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "254700000000",
            Amount = 500m,
            Currency = "KES",
            Description = "salary"
        });
        Assert.Equal("po-1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "254700000000",
            Amount = 500m,
            Currency = "KES",
            Description = "salary"
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
                {"status":"success","transaction_id":"po-1"}
                """);
        });
        var cache = new InMemoryBhenguDistributedCache();
        var provider = Create(handler, cache);
        var request = new PayoutRequest
        {
            DestinationToken = "254700000000",
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

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeSucceededEvent_ForMagicSuccessCode()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"txncd":"txn-1","status":"aei7p7yrx4ae34","amount":"100","currency":"KES"}
            """);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(100m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeFailedEvent_ForFailedCode()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("txncd=txn-2&status=bdi6p2yy76etrs&amount=50&currency=KES");
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsPayoutCompletedEvent_ForPayoutEvent()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"txncd":"po-1","event":"payout.completed","amount":"500","currency":"KES","status":"any"}
            """);
        var typed = Assert.IsType<PayoutCompletedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutCompleted, typed.Category);
    }
}
