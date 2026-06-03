// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.PagSeguro.Configuration;
using Bhengu.Finance.Payments.PagSeguro.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PagSeguro;

/// <summary>
/// Verifies <see cref="PagSeguroPaymentProvider.ParseWebhookAsync"/> returns the typed
/// sub-record for every supported PagBank event family.
/// </summary>
public class PagSeguroTypedWebhookTests
{
    private static PagSeguroPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new PagSeguroOptions { ApiToken = "pagbank-test-token", WebhookSecret = "wsec" }),
            NullLogger<PagSeguroPaymentProvider>.Instance);

    [Fact]
    public async Task ParseWebhook_OrderCreated_ReturnsChargePendingEvent()
    {
        var json = """{"id":"ORDE_1","event":"ORDER.created","charges":[{"id":"CHAR_1","status":"WAITING","amount":{"value":10000,"currency":"BRL"}}]}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargePendingEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargePending, typed.Category);
        Assert.Equal("ORDE_1", typed.GatewayReference);
        Assert.Equal(100m, typed.Amount);
        Assert.Equal("BRL", typed.Currency);
    }

    [Fact]
    public async Task ParseWebhook_ChargePaid_ReturnsChargeSucceededEvent()
    {
        var json = """{"id":"ORDE_2","event":"CHARGE.PAID","charges":[{"id":"CHAR_2","status":"PAID","amount":{"value":10000,"currency":"BRL"}}]}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(PaymentStatus.Completed, typed.Status);
        Assert.Equal(100m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhook_ChargeDeclined_ReturnsChargeFailedEvent()
    {
        var json = """{"id":"ORDE_3","event":"CHARGE.DECLINED","charges":[{"id":"CHAR_3","status":"DECLINED","amount":{"value":10000,"currency":"BRL"}}]}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal("declined", typed.FailureCode);
    }

    [Fact]
    public async Task ParseWebhook_ChargeCanceled_DirectCharge_ReturnsChargeFailedEvent()
    {
        var json = """{"id":"ORDE_4","event":"CHARGE.CANCELED","charges":[{"id":"CHAR_4","status":"CANCELED","amount":{"value":10000,"currency":"BRL"}}]}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(PaymentStatus.Cancelled, typed.Status);
        Assert.Equal("cancelled", typed.FailureCode);
    }

    [Fact]
    public async Task ParseWebhook_ChargeCanceled_Recurring_ReturnsSubscriptionCancelledEvent()
    {
        // When the order has a "pg_plan_" reference_id it originated from a recurring subscription;
        // CHARGE.CANCELED should surface as a subscription cancellation, not a one-off failure.
        var json = """{"id":"RECU_1","event":"CHARGE.CANCELED","reference_id":"pg_plan_abc","charges":[{"id":"CHAR_5","status":"CANCELED","amount":{"value":9990,"currency":"BRL"}}]}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<SubscriptionCancelledEvent>(evt);
        Assert.Equal(WebhookEventCategory.SubscriptionCancelled, typed.Category);
        Assert.Equal("RECU_1", typed.SubscriptionReference);
    }

    [Fact]
    public async Task ParseWebhook_RefundProcessed_ReturnsRefundSucceededEvent()
    {
        var json = """{"id":"ORDE_6","event":"REFUND.PROCESSED","charges":[{"id":"CHAR_6","status":"REFUNDED","amount":{"value":5000,"currency":"BRL"}}]}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<RefundSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundSucceeded, typed.Category);
        Assert.Equal("CHAR_6", typed.RefundReference);
        Assert.Equal(50m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhook_LegacyChargeShape_StillReturnsTyped()
    {
        // No `event` field — the older webhook contract only carried the order/charge status. The
        // typed mapper should still resolve to ChargeSucceeded.
        var json = """{"id":"ORDE_99","status":"PAID","charges":[{"id":"CHAR_99","status":"PAID","amount":{"value":10000,"currency":"BRL"}}]}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal("ORDE_99", typed.GatewayReference);
    }
}
