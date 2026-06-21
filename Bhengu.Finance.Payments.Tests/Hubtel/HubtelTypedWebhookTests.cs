// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Hubtel.Configuration;
using Bhengu.Finance.Payments.Hubtel.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Hubtel;

public class HubtelTypedWebhookTests
{
    private static HubtelPaymentProvider Create(StubHttpMessageHandler handler, IBhenguDistributedCache? cache = null) =>
        new(new HttpClient(handler),
            Options.Create(new HubtelOptions
            {
                ClientId = "ci",
                ClientSecret = "cs",
                MerchantAccountNumber = "POS-1",
                WebhookSecret = "whsec",
                Currency = "GHS"
            }),
            NullLogger<HubtelPaymentProvider>.Instance,
            cache);

    [Fact]
    public void Capabilities_AdvertisesTypedWebhooks_Idempotency_Settlement()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.TypedWebhooks));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.Idempotency));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.Settlement));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeSucceededEvent_ForPaidStatus()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"type":"payment.completed","data":{"clientReference":"client-1","transactionId":"tx-99","status":"success","amount":12.5,"currency":"GHS"}}
            """);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(12.5m, typed.Amount);
        Assert.Equal("GHS", typed.Currency);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsRefundSucceededEvent_ForRefundType()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"type":"refund.completed","data":{"clientReference":"client-1","transactionId":"rf-1","status":"success","amount":5,"currency":"GHS"}}
            """);
        var typed = Assert.IsType<RefundSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundSucceeded, typed.Category);
        Assert.Equal("rf-1", typed.RefundReference);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsPayoutCompletedEvent_ForPayoutType()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"type":"payout.completed","data":{"clientReference":"po-1","transactionId":"tx-po","status":"success","amount":100,"currency":"GHS"}}
            """);
        var typed = Assert.IsType<PayoutCompletedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutCompleted, typed.Category);
    }

    [Fact]
    public async Task ParseWebhookAsync_ParsesRealHubtelPascalCaseCallback_AsChargeSucceeded()
    {
        // The REAL Hubtel Online Checkout callback: PascalCase, no "type", ResponseCode "0000",
        // status + identifiers under Data. Source:
        // https://businessdocs-developers.hubtel.com/reference/checkout-callback
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"ResponseCode":"0000","Status":"Success","Data":{"CheckoutId":"59e2fbbff4e443b98e09346881ac7e9a","SalesInvoiceId":"e96ccfb4746045bba13f425bd573a31c","ClientReference":"Kaks545253","Status":"Success","Amount":0.5,"CustomerPhoneNumber":"233242825109","PaymentDetails":{"MobileMoneyNumber":"233242825109","PaymentType":"mobilemoney","Channel":"mtn-gh"},"Description":"The MTN Mobile Money payment has been approved and processed successfully."}}
            """);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal("Kaks545253", typed.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, typed.Status);
        Assert.Equal(0.5m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeFailedEvent_ForFailedStatus()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"type":"payment.failed","data":{"clientReference":"c","transactionId":"x","status":"failed","amount":10,"currency":"GHS"}}
            """);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
    }

    [Fact]
    public async Task ProcessPaymentAsync_HonoursIdempotencyKey_ViaCache()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"responseCode":"0000","status":"Success","data":{"checkoutUrl":"https://checkout.hubtel.com/abc","checkoutId":"ck-1","clientReference":"cli"}}
                """);
        });
        var cache = new InMemoryBhenguDistributedCache();
        var provider = Create(handler, cache);
        var request = new PaymentRequest
        {
            PaymentMethodToken = "cli-1",
            Amount = 12.50m,
            Currency = "GHS",
            Description = "test",
            IdempotencyKey = "idem-1"
        };

        var first = await provider.ProcessPaymentAsync(request);
        var second = await provider.ProcessPaymentAsync(request);
        Assert.Equal(1, calls);
        Assert.Equal(first.GatewayReference, second.GatewayReference);
        Assert.Equal(first.RedirectUrl, second.RedirectUrl);
    }

    [Fact]
    public async Task ProcessPayoutAsync_HonoursIdempotencyKey_ViaCache()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"responseCode":"0000","status":"Success","data":{"transactionId":"po-1","transactionStatus":"success"}}
                """);
        });
        var cache = new InMemoryBhenguDistributedCache();
        var provider = Create(handler, cache);
        var request = new PayoutRequest
        {
            DestinationToken = "mtn-gh:233244000000",
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
}
