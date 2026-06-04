// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Paytm.Configuration;
using Bhengu.Finance.Payments.Paytm.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paytm;

public class PaytmTypedWebhookTests
{
    private static PaytmPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new PaytmOptions { MerchantId = "MID1", MerchantKey = "secret_key" }),
            NullLogger<PaytmPaymentProvider>.Instance);

    [Fact]
    public async Task ParseWebhook_TxnSuccess_ReturnsChargeSucceededEvent()
    {
        var json = """{"ORDERID":"ORDER1","STATUS":"TXN_SUCCESS","TXNID":"T123","TXNAMOUNT":"100.00","CURRENCY":"INR"}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal("ORDER1", typed.GatewayReference);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(100m, typed.Amount);
        Assert.Equal("T123", typed.PaymentMethodToken);
    }

    [Fact]
    public async Task ParseWebhook_TxnFailure_ReturnsChargeFailedEvent()
    {
        var json = """{"ORDERID":"ORDER2","STATUS":"TXN_FAILURE","RESPCODE":"401","RESPMSG":"Insufficient","TXNAMOUNT":"100.00","CURRENCY":"INR"}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal("401", typed.FailureCode);
        Assert.Equal("Insufficient", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhook_Pending_ReturnsChargePendingEvent()
    {
        var json = """{"orderId":"ORDER3","status":"PENDING","txnAmount":"50.00","currency":"INR"}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargePendingEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargePending, typed.Category);
        Assert.Equal(PaymentStatus.Pending, typed.Status);
    }

    [Fact]
    public async Task ParseWebhook_Refunded_ReturnsRefundSucceededEvent()
    {
        var json = """{"ORDERID":"ORDER4","STATUS":"REFUND_SUCCESS","REFUNDID":"R1","TXNAMOUNT":"25.00","CURRENCY":"INR"}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<RefundSucceededEvent>(evt);
        Assert.Equal("R1", typed.RefundReference);
        Assert.Equal(25m, typed.Amount);
        Assert.Equal(WebhookEventCategory.RefundSucceeded, typed.Category);
    }

    [Fact]
    public async Task ParseWebhook_BothNamingSchemes_Equivalent()
    {
        var upper = await Create().ParseWebhookAsync("""{"ORDERID":"ORDER5","STATUS":"TXN_SUCCESS"}""");
        var lower = await Create().ParseWebhookAsync("""{"orderId":"ORDER5","status":"TXN_SUCCESS"}""");
        Assert.IsType<ChargeSucceededEvent>(upper);
        Assert.IsType<ChargeSucceededEvent>(lower);
        Assert.Equal(upper!.GatewayReference, lower!.GatewayReference);
    }

    [Fact]
    public async Task ParseWebhook_UnknownStatus_ReturnsBaseEvent()
    {
        var json = """{"ORDERID":"ORDER6","STATUS":"WAITING_REVIEW"}""";
        var evt = await Create().ParseWebhookAsync(json);
        Assert.NotNull(evt);
        Assert.IsType<WebhookEvent>(evt);
    }
}
