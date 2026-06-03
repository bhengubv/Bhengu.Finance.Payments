// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Stripe;

public class StripeTypedWebhookTests
{
    private static StripePaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new StripeOptions { SecretKey = "sk_test_fake", WebhookSecret = "whsec_test_fake" }),
            NullLogger<StripePaymentProvider>.Instance);

    [Fact]
    public void Capabilities_IncludeAllNewFlags()
    {
        var p = Create();
        Assert.True(p.Capabilities.HasFlag(ProviderCapabilities.Tokenisation));
        Assert.True(p.Capabilities.HasFlag(ProviderCapabilities.Subscriptions));
        Assert.True(p.Capabilities.HasFlag(ProviderCapabilities.ThreeDSecure));
        Assert.True(p.Capabilities.HasFlag(ProviderCapabilities.Disputes));
        Assert.True(p.Capabilities.HasFlag(ProviderCapabilities.Settlement));
        Assert.True(p.Capabilities.HasFlag(ProviderCapabilities.Mandates));
        Assert.True(p.Capabilities.HasFlag(ProviderCapabilities.Marketplace));
        Assert.True(p.Capabilities.HasFlag(ProviderCapabilities.Idempotency));
        Assert.True(p.Capabilities.HasFlag(ProviderCapabilities.TypedWebhooks));
        Assert.True(p.Capabilities.HasFlag(ProviderCapabilities.PartialRefund));
    }

    [Fact]
    public async Task ParseWebhookAsync_ChargeSucceeded_ReturnsTypedEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"id":"evt_1","object":"event","api_version":"2024-06-20","created":1700000000,"livemode":false,"pending_webhooks":1,"request":{"id":"req_x","idempotency_key":null},"type":"charge.succeeded","data":{"object":{"id":"ch_1","object":"charge","amount":9999,"currency":"usd","customer":"cus_1","payment_method":"pm_1","payment_intent":"pi_1","status":"succeeded"}}}
            """);

        Assert.IsType<ChargeSucceededEvent>(evt);
        var typed = (ChargeSucceededEvent)evt!;
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal("pi_1", typed.GatewayReference);
        Assert.Equal(99.99m, typed.Amount);
        Assert.Equal("USD", typed.Currency);
        Assert.Equal("cus_1", typed.CustomerId);
        Assert.Equal("pm_1", typed.PaymentMethodToken);
    }

    [Fact]
    public async Task ParseWebhookAsync_ChargeFailed_ReturnsTypedEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"id":"evt_2","object":"event","api_version":"2024-06-20","created":1700000000,"livemode":false,"pending_webhooks":1,"request":{"id":"req_x","idempotency_key":null},"type":"charge.failed","data":{"object":{"id":"ch_2","object":"charge","amount":5000,"currency":"usd","payment_intent":"pi_2","failure_code":"card_declined","failure_message":"Card declined","status":"failed"}}}
            """);

        Assert.IsType<ChargeFailedEvent>(evt);
        var typed = (ChargeFailedEvent)evt!;
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal("card_declined", typed.FailureCode);
        Assert.Equal("Card declined", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhookAsync_ChargeRefunded_ReturnsRefundSucceeded()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"id":"evt_3","object":"event","api_version":"2024-06-20","created":1700000000,"livemode":false,"pending_webhooks":1,"request":{"id":"req_x","idempotency_key":null},"type":"charge.refunded","data":{"object":{"id":"ch_3","object":"charge","amount":10000,"amount_refunded":3000,"currency":"usd","payment_intent":"pi_3","status":"succeeded","refunds":{"object":"list","data":[{"id":"re_3","object":"refund","amount":3000,"currency":"usd","status":"succeeded"}]}}}}
            """);

        Assert.IsType<RefundSucceededEvent>(evt);
        var typed = (RefundSucceededEvent)evt!;
        Assert.Equal(WebhookEventCategory.RefundSucceeded, typed.Category);
        Assert.Equal("re_3", typed.RefundReference);
        Assert.Equal(30m, typed.Amount);
        Assert.True(typed.IsPartial);
    }

    [Fact]
    public async Task ParseWebhookAsync_DisputeCreated_ReturnsDisputeOpenedEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"id":"evt_4","object":"event","api_version":"2024-06-20","created":1700000000,"livemode":false,"pending_webhooks":1,"request":{"id":"req_x","idempotency_key":null},"type":"charge.dispute.created","data":{"object":{"id":"dp_4","object":"dispute","amount":15000,"currency":"usd","charge":"ch_4","reason":"fraudulent","status":"needs_response","evidence_details":{"due_by":1702592000,"has_evidence":false,"past_due":false,"submission_count":0}}}}
            """);

        Assert.IsType<DisputeOpenedEvent>(evt);
        var typed = (DisputeOpenedEvent)evt!;
        Assert.Equal(WebhookEventCategory.DisputeOpened, typed.Category);
        Assert.Equal("dp_4", typed.DisputeReference);
        Assert.Equal(150m, typed.Amount);
        Assert.Equal("fraudulent", typed.ReasonCode);
        Assert.NotNull(typed.EvidenceDueBy);
    }

    [Fact]
    public async Task ParseWebhookAsync_DisputeWon_ReturnsDisputeWonEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"id":"evt_5","object":"event","api_version":"2024-06-20","created":1700000000,"livemode":false,"pending_webhooks":1,"request":{"id":"req_x","idempotency_key":null},"type":"charge.dispute.closed","data":{"object":{"id":"dp_5","object":"dispute","amount":15000,"currency":"usd","charge":"ch_5","reason":"fraudulent","status":"won"}}}
            """);

        Assert.IsType<DisputeWonEvent>(evt);
        Assert.Equal(WebhookEventCategory.DisputeWon, evt.Category);
    }

    [Fact]
    public async Task ParseWebhookAsync_DisputeLost_ReturnsDisputeLostEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"id":"evt_6","object":"event","api_version":"2024-06-20","created":1700000000,"livemode":false,"pending_webhooks":1,"request":{"id":"req_x","idempotency_key":null},"type":"charge.dispute.closed","data":{"object":{"id":"dp_6","object":"dispute","amount":15000,"currency":"usd","charge":"ch_6","reason":"fraudulent","status":"lost"}}}
            """);

        Assert.IsType<DisputeLostEvent>(evt);
        Assert.Equal(WebhookEventCategory.DisputeLost, evt.Category);
    }

    [Fact]
    public async Task ParseWebhookAsync_SubscriptionCreated_ReturnsTypedEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"id":"evt_7","object":"event","api_version":"2024-06-20","created":1700000000,"livemode":false,"pending_webhooks":1,"request":{"id":"req_x","idempotency_key":null},"type":"customer.subscription.created","data":{"object":{"id":"sub_7","object":"subscription","customer":"cus_7","status":"active","start_date":1700000000,"current_period_end":1702592000,"items":{"object":"list","data":[{"id":"si_7","plan":{"id":"plan_pro"}}]}}}}
            """);

        Assert.IsType<SubscriptionCreatedEvent>(evt);
        var typed = (SubscriptionCreatedEvent)evt!;
        Assert.Equal(WebhookEventCategory.SubscriptionCreated, typed.Category);
        Assert.Equal("sub_7", typed.SubscriptionReference);
        Assert.Equal("plan_pro", typed.PlanReference);
        Assert.Equal("cus_7", typed.CustomerId);
    }

    [Fact]
    public async Task ParseWebhookAsync_SubscriptionRenewed_ReturnsTypedEvent()
    {
        var provider = Create();
        // customer.subscription.updated with status=active is mapped to a renewal event.
        var evt = await provider.ParseWebhookAsync("""
            {"id":"evt_8","object":"event","api_version":"2024-06-20","created":1700000000,"livemode":false,"pending_webhooks":1,"request":{"id":"req_x","idempotency_key":null},"type":"customer.subscription.updated","data":{"object":{"id":"sub_8","object":"subscription","customer":"cus_8","status":"active","currency":"usd","start_date":1700000000,"current_period_end":1702592000,"items":{"object":"list","data":[{"id":"si_8","plan":{"id":"plan_pro","amount":9999,"currency":"usd","interval":"month","interval_count":1}}]}}}}
            """);

        Assert.IsType<SubscriptionRenewedEvent>(evt);
        var typed = (SubscriptionRenewedEvent)evt!;
        Assert.Equal(WebhookEventCategory.SubscriptionRenewed, typed.Category);
        Assert.Equal(99.99m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_SubscriptionDeleted_ReturnsCancelledEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"id":"evt_9","object":"event","api_version":"2024-06-20","created":1700000000,"livemode":false,"pending_webhooks":1,"request":{"id":"req_x","idempotency_key":null},"type":"customer.subscription.deleted","data":{"object":{"id":"sub_9","object":"subscription","customer":"cus_9","status":"canceled","start_date":1700000000,"cancellation_details":{"reason":"cancellation_requested"},"items":{"object":"list","data":[{"id":"si_9","plan":{"id":"plan_pro"}}]}}}}
            """);

        Assert.IsType<SubscriptionCancelledEvent>(evt);
        var typed = (SubscriptionCancelledEvent)evt!;
        Assert.Equal(WebhookEventCategory.SubscriptionCancelled, typed.Category);
        Assert.Equal(PaymentStatus.Cancelled, typed.Status);
        Assert.Equal("cancellation_requested", typed.CancellationReason);
    }

    [Fact]
    public async Task ParseWebhookAsync_PayoutPaid_ReturnsCompletedEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"id":"evt_10","object":"event","api_version":"2024-06-20","created":1700000000,"livemode":false,"pending_webhooks":1,"request":{"id":"req_x","idempotency_key":null},"type":"payout.paid","data":{"object":{"id":"po_10","object":"payout","amount":250000,"currency":"usd","arrival_date":1700000000,"destination":"ba_10","status":"paid"}}}
            """);

        Assert.IsType<PayoutCompletedEvent>(evt);
        var typed = (PayoutCompletedEvent)evt!;
        Assert.Equal(WebhookEventCategory.PayoutCompleted, typed.Category);
        Assert.Equal("po_10", typed.PayoutReference);
        Assert.Equal(2500m, typed.Amount);
        Assert.Equal("ba_10", typed.DestinationToken);
    }

    [Fact]
    public async Task ParseWebhookAsync_PayoutFailed_ReturnsFailedEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"id":"evt_11","object":"event","api_version":"2024-06-20","created":1700000000,"livemode":false,"pending_webhooks":1,"request":{"id":"req_x","idempotency_key":null},"type":"payout.failed","data":{"object":{"id":"po_11","object":"payout","amount":100000,"currency":"usd","arrival_date":1700000000,"destination":"ba_11","status":"failed","failure_code":"invalid_account_number","failure_message":"Invalid bank account"}}}
            """);

        Assert.IsType<PayoutFailedEvent>(evt);
        var typed = (PayoutFailedEvent)evt!;
        Assert.Equal(WebhookEventCategory.PayoutFailed, typed.Category);
        Assert.Equal("invalid_account_number", typed.FailureCode);
    }

    [Fact]
    public async Task ParseWebhookAsync_UnknownEventType_ReturnsNull()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"id":"evt_z","object":"event","api_version":"2024-06-20","created":1700000000,"livemode":false,"pending_webhooks":1,"request":{"id":"req_x","idempotency_key":null},"type":"product.created","data":{"object":{"id":"prod_z"}}}
            """);
        Assert.Null(evt);
    }
}
