// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.MercadoPago.Configuration;
using Bhengu.Finance.Payments.MercadoPago.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.MercadoPago;

/// <summary>
/// Verifies <see cref="MercadoPagoPaymentProvider.ParseWebhookAsync"/> returns the typed
/// sub-record for every supported Mercado Pago topic/action combination plus the right
/// <see cref="WebhookEventCategory"/>.
/// </summary>
public class MercadoPagoTypedWebhookTests
{
    private static MercadoPagoPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new MercadoPagoOptions { AccessToken = "TEST-x", WebhookSecret = "wsec" }),
            NullLogger<MercadoPagoPaymentProvider>.Instance);

    [Fact]
    public async Task ParseWebhook_PaymentCreated_ReturnsChargePendingEvent()
    {
        var json = """{"type":"payment","action":"payment.created","data":{"id":"99999","status":"pending","transaction_amount":100.00,"currency_id":"BRL"}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargePendingEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargePending, typed.Category);
        Assert.Equal("99999", typed.GatewayReference);
        Assert.Equal(100m, typed.Amount);
        Assert.Equal("BRL", typed.Currency);
    }

    [Fact]
    public async Task ParseWebhook_PaymentUpdatedApproved_ReturnsChargeSucceededEvent()
    {
        var json = """{"type":"payment","action":"payment.updated","data":{"id":"99999","status":"approved","transaction_amount":100.00,"currency_id":"BRL"}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(PaymentStatus.Completed, typed.Status);
        Assert.Equal(100m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhook_PaymentUpdatedRejected_ReturnsChargeFailedEvent()
    {
        var json = """{"type":"payment","action":"payment.updated","data":{"id":"55555","status":"rejected","status_detail":"cc_rejected_insufficient_amount","transaction_amount":75.00,"currency_id":"BRL"}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal("cc_rejected_insufficient_amount", typed.FailureCode);
    }

    [Fact]
    public async Task ParseWebhook_PaymentUpdatedRefunded_ReturnsRefundSucceededEvent()
    {
        var json = """{"type":"payment","action":"payment.updated","data":{"id":"R-1","status":"refunded","transaction_amount":75.00,"currency_id":"BRL"}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<RefundSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundSucceeded, typed.Category);
        Assert.Equal("R-1", typed.RefundReference);
        Assert.Equal("R-1", typed.GatewayReference);
        Assert.Equal(75m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhook_PreapprovalUpdatedCancelled_ReturnsSubscriptionCancelledEvent()
    {
        var json = """{"type":"preapproval","action":"updated","data":{"id":"pa-1","status":"cancelled"}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<SubscriptionCancelledEvent>(evt);
        Assert.Equal(WebhookEventCategory.SubscriptionCancelled, typed.Category);
        Assert.Equal("pa-1", typed.SubscriptionReference);
        Assert.Equal("cancelled", typed.CancellationReason);
    }

    [Fact]
    public async Task ParseWebhook_PreapprovalUpdatedPaused_ReturnsSubscriptionCancelledEventWithPausedReason()
    {
        var json = """{"type":"preapproval","action":"updated","data":{"id":"pa-2","status":"paused"}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<SubscriptionCancelledEvent>(evt);
        Assert.Equal(WebhookEventCategory.SubscriptionCancelled, typed.Category);
        Assert.Equal("paused", typed.CancellationReason);
    }

    [Fact]
    public async Task ParseWebhook_SubscriptionAuthorizedPayment_ReturnsSubscriptionRenewedEvent()
    {
        var json = """{"type":"subscription_authorized_payment","action":"created","data":{"id":"sap-1","status":"approved","transaction_amount":99.90,"currency_id":"BRL"}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<SubscriptionRenewedEvent>(evt);
        Assert.Equal(WebhookEventCategory.SubscriptionRenewed, typed.Category);
        Assert.Equal("sap-1", typed.SubscriptionReference);
        Assert.Equal(99.90m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhook_Unknown_ReturnsNull()
    {
        var json = """{"type":"merchant_order","action":"updated","data":{"id":"mo-1"}}""";
        Assert.Null(await Create().ParseWebhookAsync(json));
    }

    [Fact]
    public async Task ParseWebhook_InvalidJson_ReturnsNull()
    {
        Assert.Null(await Create().ParseWebhookAsync("not json"));
    }
}
