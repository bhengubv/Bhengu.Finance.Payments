// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Bhengu.Finance.Payments.Razorpay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Razorpay;

/// <summary>
/// Verifies that <see cref="RazorpayPaymentProvider.ParseWebhookAsync"/> returns the typed
/// sub-record for every supported Razorpay event family — and the right <see cref="WebhookEventCategory"/>.
/// </summary>
public class RazorpayTypedWebhookTests
{
    private static RazorpayPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new RazorpayOptions { KeyId = "rzp_test_x", KeySecret = "secret_x", WebhookSecret = "wsec" }),
            NullLogger<RazorpayPaymentProvider>.Instance);

    [Fact]
    public async Task ParseWebhook_PaymentCaptured_ReturnsChargeSucceededEvent()
    {
        var json = """{"event":"payment.captured","payload":{"payment":{"entity":{"id":"pay_1","amount":10000,"currency":"INR","status":"captured","customer_id":"cust_1","token_id":"tkn_1"}}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal("pay_1", typed.GatewayReference);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(100m, typed.Amount);
        Assert.Equal("INR", typed.Currency);
        Assert.Equal("cust_1", typed.CustomerId);
        Assert.Equal("tkn_1", typed.PaymentMethodToken);
    }

    [Fact]
    public async Task ParseWebhook_PaymentFailed_ReturnsChargeFailedEvent()
    {
        var json = """{"event":"payment.failed","payload":{"payment":{"entity":{"id":"pay_2","amount":10000,"currency":"INR","status":"failed","error_code":"BAD_REQUEST_ERROR","error_description":"Insufficient funds"}}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal("BAD_REQUEST_ERROR", typed.FailureCode);
        Assert.Equal("Insufficient funds", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhook_PaymentAuthorized_ReturnsChargePendingEvent()
    {
        var json = """{"event":"payment.authorized","payload":{"payment":{"entity":{"id":"pay_3","amount":10000,"currency":"INR","status":"authorized"}}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargePendingEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargePending, typed.Category);
        Assert.Equal(PaymentStatus.Pending, typed.Status);
    }

    [Fact]
    public async Task ParseWebhook_RefundProcessed_ReturnsRefundSucceededEvent()
    {
        var json = """{"event":"refund.processed","payload":{"refund":{"entity":{"id":"rfnd_1","amount":5000,"currency":"INR","payment_id":"pay_99","status":"processed"}}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<RefundSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundSucceeded, typed.Category);
        Assert.Equal("rfnd_1", typed.RefundReference);
        Assert.Equal("pay_99", typed.GatewayReference);
        Assert.Equal(50m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhook_RefundFailed_ReturnsRefundFailedEvent()
    {
        var json = """{"event":"refund.failed","payload":{"refund":{"entity":{"id":"rfnd_2","amount":5000,"currency":"INR","payment_id":"pay_99","status":"failed"}}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<RefundFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundFailed, typed.Category);
    }

    [Fact]
    public async Task ParseWebhook_SubscriptionActivated_ReturnsSubscriptionCreatedEvent()
    {
        var json = """{"event":"subscription.activated","payload":{"subscription":{"entity":{"id":"sub_1","plan_id":"plan_1","customer_id":"cust_1","status":"active","charge_at":1700000000}}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<SubscriptionCreatedEvent>(evt);
        Assert.Equal(WebhookEventCategory.SubscriptionCreated, typed.Category);
        Assert.Equal("sub_1", typed.SubscriptionReference);
        Assert.Equal("plan_1", typed.PlanReference);
    }

    [Fact]
    public async Task ParseWebhook_SubscriptionCharged_ReturnsSubscriptionRenewedEvent()
    {
        var json = """{"event":"subscription.charged","payload":{"subscription":{"entity":{"id":"sub_1","plan_id":"plan_1","status":"active","charge_at":1700100000}},"payment":{"entity":{"id":"pay_r1","amount":99900,"currency":"INR","status":"captured"}}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<SubscriptionRenewedEvent>(evt);
        Assert.Equal(WebhookEventCategory.SubscriptionRenewed, typed.Category);
        Assert.Equal(999m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhook_SubscriptionCancelled_ReturnsSubscriptionCancelledEvent()
    {
        var json = """{"event":"subscription.cancelled","payload":{"subscription":{"entity":{"id":"sub_1","plan_id":"plan_1","status":"cancelled"}}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<SubscriptionCancelledEvent>(evt);
        Assert.Equal(WebhookEventCategory.SubscriptionCancelled, typed.Category);
    }

    [Fact]
    public async Task ParseWebhook_SubscriptionPending_ReturnsSubscriptionChargeFailedEvent()
    {
        var json = """{"event":"subscription.pending","payload":{"subscription":{"entity":{"id":"sub_2","plan_id":"plan_1","status":"pending"}}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<SubscriptionChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.SubscriptionChargeFailed, typed.Category);
    }

    [Fact]
    public async Task ParseWebhook_PayoutProcessed_ReturnsPayoutCompletedEvent()
    {
        var json = """{"event":"payout.processed","payload":{"payout":{"entity":{"id":"pout_1","amount":50000,"currency":"INR","status":"processed"}}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<PayoutCompletedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutCompleted, typed.Category);
        Assert.Equal(500m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhook_PayoutFailed_ReturnsPayoutFailedEvent()
    {
        var json = """{"event":"payout.failed","payload":{"payout":{"entity":{"id":"pout_2","amount":50000,"currency":"INR","status":"failed","failure_reason":"invalid_account"}}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<PayoutFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutFailed, typed.Category);
        Assert.Equal("invalid_account", typed.FailureCode);
    }

    [Fact]
    public async Task ParseWebhook_PayoutReversed_ReturnsPayoutFailedEvent()
    {
        var json = """{"event":"payout.reversed","payload":{"payout":{"entity":{"id":"pout_3","amount":50000,"currency":"INR","status":"reversed"}}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<PayoutFailedEvent>(evt);
        Assert.Equal("reversed", typed.FailureCode);
    }

    [Fact]
    public async Task ParseWebhook_SettlementProcessed_ReturnsSettlementCompletedEvent()
    {
        var json = """{"event":"settlement.processed","payload":{"settlement":{"entity":{"id":"setl_1","amount":100000,"fees":1180,"tax":212}}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<SettlementCompletedEvent>(evt);
        Assert.Equal(WebhookEventCategory.SettlementCompleted, typed.Category);
        Assert.Equal(1000m, typed.NetAmount);
    }

    [Fact]
    public async Task ParseWebhook_TokenConfirmed_ReturnsMandateActivatedEvent()
    {
        var json = """{"event":"token.confirmed","payload":{"token":{"entity":{"id":"token_m","max_amount":500000,"recurring_status":"confirmed"}}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<MandateActivatedEvent>(evt);
        Assert.Equal(WebhookEventCategory.MandateActivated, typed.Category);
        Assert.Equal("token_m", typed.MandateReference);
        Assert.Equal(5000m, typed.AmountLimit);
    }

    [Fact]
    public async Task ParseWebhook_TokenCancelled_ReturnsMandateCancelledEvent()
    {
        var json = """{"event":"token.cancelled","payload":{"token":{"entity":{"id":"token_m","recurring_status":"cancelled"}}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<MandateCancelledEvent>(evt);
        Assert.Equal(WebhookEventCategory.MandateCancelled, typed.Category);
    }

    [Fact]
    public async Task ParseWebhook_DisputeCreated_ReturnsDisputeOpenedEvent()
    {
        var json = """{"event":"dispute.created","payload":{"dispute":{"entity":{"id":"disp_1","payment_id":"pay_1","amount":100000,"currency":"INR","reason_code":"fraudulent","respond_by":1700100000}}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<DisputeOpenedEvent>(evt);
        Assert.Equal(WebhookEventCategory.DisputeOpened, typed.Category);
        Assert.Equal("disp_1", typed.DisputeReference);
        Assert.Equal("pay_1", typed.GatewayReference);
        Assert.Equal(1000m, typed.Amount);
        Assert.Equal("fraudulent", typed.ReasonCode);
    }

    [Fact]
    public async Task ParseWebhook_DisputeWon_ReturnsDisputeWonEvent()
    {
        var json = """{"event":"dispute.won","payload":{"dispute":{"entity":{"id":"disp_2","payment_id":"pay_2","amount":100000,"currency":"INR"}}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<DisputeWonEvent>(evt);
        Assert.Equal(WebhookEventCategory.DisputeWon, typed.Category);
    }

    [Fact]
    public async Task ParseWebhook_DisputeLost_ReturnsDisputeLostEvent()
    {
        var json = """{"event":"dispute.lost","payload":{"dispute":{"entity":{"id":"disp_3","payment_id":"pay_3","amount":100000,"currency":"INR"}}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<DisputeLostEvent>(evt);
        Assert.Equal(WebhookEventCategory.DisputeLost, typed.Category);
    }

    [Fact]
    public async Task ParseWebhook_UnknownEvent_ReturnsNull()
    {
        var json = """{"event":"order.notifying_party","payload":{}}""";
        Assert.Null(await Create().ParseWebhookAsync(json));
    }

    [Fact]
    public async Task ParseWebhook_OrderPaid_LegacyMapping_StillReturns()
    {
        var json = """{"event":"order.paid","payload":{"payment":{"entity":{"id":"pay_legacy"}}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        Assert.NotNull(evt);
        Assert.Equal("pay_legacy", evt!.GatewayReference);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, evt.Category);
    }
}
