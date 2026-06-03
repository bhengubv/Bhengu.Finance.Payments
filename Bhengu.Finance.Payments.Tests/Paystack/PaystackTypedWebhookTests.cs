// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paystack;

public class PaystackTypedWebhookTests
{
    private static PaystackPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new PaystackOptions { SecretKey = "sk", DefaultEmail = "b@x.com" }),
            NullLogger<PaystackPaymentProvider>.Instance);

    [Fact]
    public void Capabilities_IncludeAllNewFlags()
    {
        var p = Create();
        Assert.True(p.Capabilities.HasFlag(ProviderCapabilities.Tokenisation));
        Assert.True(p.Capabilities.HasFlag(ProviderCapabilities.Subscriptions));
        Assert.True(p.Capabilities.HasFlag(ProviderCapabilities.Disputes));
        Assert.True(p.Capabilities.HasFlag(ProviderCapabilities.Settlement));
        Assert.True(p.Capabilities.HasFlag(ProviderCapabilities.Marketplace));
        Assert.True(p.Capabilities.HasFlag(ProviderCapabilities.Payout));
        Assert.True(p.Capabilities.HasFlag(ProviderCapabilities.Idempotency));
        Assert.True(p.Capabilities.HasFlag(ProviderCapabilities.TypedWebhooks));
        Assert.True(p.Capabilities.HasFlag(ProviderCapabilities.PartialRefund));
    }

    [Fact]
    public async Task ParseWebhookAsync_ChargeSuccess_ReturnsChargeSucceededEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"charge.success","data":{"id":1,"reference":"ref_1","status":"success","amount":10000,"currency":"NGN","customer":{"customer_code":"CUS_a"},"authorization":{"authorization_code":"AUTH_a"}}}
            """);
        Assert.IsType<ChargeSucceededEvent>(evt);
        var typed = (ChargeSucceededEvent)evt!;
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(100m, typed.Amount);
        Assert.Equal("NGN", typed.Currency);
        Assert.Equal("CUS_a", typed.CustomerId);
        Assert.Equal("AUTH_a", typed.PaymentMethodToken);
    }

    [Fact]
    public async Task ParseWebhookAsync_ChargeFailed_ReturnsChargeFailedEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"charge.failed","data":{"reference":"ref_2","status":"failed","amount":15000,"currency":"NGN","gateway_response":"insufficient_funds","message":"declined"}}
            """);
        Assert.IsType<ChargeFailedEvent>(evt);
        var typed = (ChargeFailedEvent)evt!;
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal("insufficient_funds", typed.FailureCode);
        Assert.Equal("declined", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhookAsync_RefundProcessed_ReturnsRefundSucceededEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"refund.processed","data":{"id":99,"reference":"ref_3","status":"processed","amount":5000,"currency":"NGN","transaction_amount":10000,"refunded_by":"merchant"}}
            """);
        Assert.IsType<RefundSucceededEvent>(evt);
        var typed = (RefundSucceededEvent)evt!;
        Assert.Equal(WebhookEventCategory.RefundSucceeded, typed.Category);
        Assert.Equal("99", typed.RefundReference);
        Assert.True(typed.IsPartial);
    }

    [Fact]
    public async Task ParseWebhookAsync_RefundFailed_ReturnsRefundFailedEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"refund.failed","data":{"reference":"ref_4","status":"failed","amount":1000,"currency":"NGN","message":"bank declined"}}
            """);
        Assert.IsType<RefundFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundFailed, evt.Category);
    }

    [Fact]
    public async Task ParseWebhookAsync_DisputeCreate_ReturnsDisputeOpenedEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"charge.dispute.create","data":{"id":50,"reference":"ref_5","amount":25000,"currency":"NGN","category":"fraud","due_at":"2026-06-30T00:00:00Z"}}
            """);
        Assert.IsType<DisputeOpenedEvent>(evt);
        var typed = (DisputeOpenedEvent)evt!;
        Assert.Equal(WebhookEventCategory.DisputeOpened, typed.Category);
        Assert.Equal("50", typed.DisputeReference);
        Assert.Equal("fraud", typed.ReasonCode);
        Assert.NotNull(typed.EvidenceDueBy);
    }

    [Fact]
    public async Task ParseWebhookAsync_DisputeResolveMerchantAccepted_ReturnsDisputeWonEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"charge.dispute.resolve","data":{"id":51,"reference":"ref_6","amount":3000,"currency":"NGN","resolution":"merchant-accepted"}}
            """);
        Assert.IsType<DisputeWonEvent>(evt);
        Assert.Equal(WebhookEventCategory.DisputeWon, evt.Category);
    }

    [Fact]
    public async Task ParseWebhookAsync_DisputeResolveLost_ReturnsDisputeLostEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"charge.dispute.resolve","data":{"id":52,"reference":"ref_7","amount":3000,"currency":"NGN","resolution":"merchant-lost"}}
            """);
        Assert.IsType<DisputeLostEvent>(evt);
        Assert.Equal(WebhookEventCategory.DisputeLost, evt.Category);
    }

    [Fact]
    public async Task ParseWebhookAsync_SubscriptionCreate_ReturnsSubscriptionCreatedEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"subscription.create","data":{"subscription_code":"SUB_a","plan":{"plan_code":"PLN_p"},"customer":{"customer_code":"CUS_c"},"next_payment_date":"2026-07-01T00:00:00Z"}}
            """);
        Assert.IsType<SubscriptionCreatedEvent>(evt);
        var typed = (SubscriptionCreatedEvent)evt!;
        Assert.Equal(WebhookEventCategory.SubscriptionCreated, typed.Category);
        Assert.Equal("SUB_a", typed.SubscriptionReference);
        Assert.Equal("PLN_p", typed.PlanReference);
        Assert.Equal("CUS_c", typed.CustomerId);
    }

    [Fact]
    public async Task ParseWebhookAsync_InvoicePaymentFailed_ReturnsSubscriptionChargeFailedEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"invoice.payment_failed","data":{"subscription":{"subscription_code":"SUB_b"},"subscription_code":"SUB_b","amount":50000,"currency":"NGN","status":"failed","next_payment_date":"2026-07-10T00:00:00Z"}}
            """);
        Assert.IsType<SubscriptionChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.SubscriptionChargeFailed, evt.Category);
    }

    [Fact]
    public async Task ParseWebhookAsync_SubscriptionDisable_ReturnsSubscriptionCancelledEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"subscription.disable","data":{"subscription_code":"SUB_c","status":"cancelled"}}
            """);
        Assert.IsType<SubscriptionCancelledEvent>(evt);
        Assert.Equal(WebhookEventCategory.SubscriptionCancelled, evt.Category);
    }

    [Fact]
    public async Task ParseWebhookAsync_TransferSuccess_ReturnsPayoutCompletedEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"transfer.success","data":{"transfer_code":"TR_x","reference":"transfer-1","amount":75000,"currency":"NGN","recipient":{"recipient_code":"RCP_d"}}}
            """);
        Assert.IsType<PayoutCompletedEvent>(evt);
        var typed = (PayoutCompletedEvent)evt!;
        Assert.Equal(WebhookEventCategory.PayoutCompleted, typed.Category);
        Assert.Equal("TR_x", typed.PayoutReference);
        Assert.Equal("RCP_d", typed.DestinationToken);
    }

    [Fact]
    public async Task ParseWebhookAsync_TransferFailed_ReturnsPayoutFailedEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"transfer.failed","data":{"transfer_code":"TR_y","amount":75000,"currency":"NGN","status":"failed","gateway_response":"unreachable_bank"}}
            """);
        Assert.IsType<PayoutFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutFailed, evt.Category);
    }

    [Fact]
    public async Task ParseWebhookAsync_TransferReversed_ReturnsPayoutFailedEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"transfer.reversed","data":{"transfer_code":"TR_z","amount":1000,"currency":"NGN","status":"reversed"}}
            """);
        Assert.IsType<PayoutFailedEvent>(evt);
    }

    [Fact]
    public async Task ParseWebhookAsync_UnknownEvent_ReturnsNull()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""{"event":"unknown.event","data":{"reference":"x"}}""");
        Assert.Null(evt);
    }
}
