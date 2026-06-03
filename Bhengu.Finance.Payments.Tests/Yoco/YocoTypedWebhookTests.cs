// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.Yoco.Configuration;
using Bhengu.Finance.Payments.Yoco.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Yoco;

public class YocoTypedWebhookTests
{
    private static YocoPaymentProvider Create()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        return new YocoPaymentProvider(
            http,
            Options.Create(new YocoOptions { SecretKey = "sk_test", WebhookSecret = "ws" }),
            NullLogger<YocoPaymentProvider>.Instance);
    }

    [Fact]
    public async Task ParseWebhookAsync_ChargeSucceeded_ReturnsTypedEvent()
    {
        var provider = Create();
        var payload = """
            {"type":"charge.succeeded","payload":{"id":"ch_1","amountInCents":12300,"currency":"ZAR","customerId":"cust-1","cardId":"card_1"}}
            """;
        var evt = await provider.ParseWebhookAsync(payload);

        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal("ch_1", typed.GatewayReference);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(123m, typed.Amount);
        Assert.Equal("ZAR", typed.Currency);
        Assert.Equal("cust-1", typed.CustomerId);
        Assert.Equal("card_1", typed.PaymentMethodToken);
    }

    [Fact]
    public async Task ParseWebhookAsync_ChargeFailed_ReturnsTypedEvent()
    {
        var provider = Create();
        var payload = """
            {"type":"charge.failed","payload":{"id":"ch_2","amountInCents":5000,"currency":"ZAR","failureCode":"declined","failureMessage":"Card declined"}}
            """;
        var evt = await provider.ParseWebhookAsync(payload);

        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal(PaymentStatus.Failed, typed.Status);
        Assert.Equal("declined", typed.FailureCode);
    }

    [Fact]
    public async Task ParseWebhookAsync_RefundSucceeded_ReturnsTypedEvent()
    {
        var provider = Create();
        var payload = """
            {"type":"refund.succeeded","payload":{"id":"rf_1","chargeId":"ch_1","amountInCents":2500,"currency":"ZAR","isPartial":true}}
            """;
        var evt = await provider.ParseWebhookAsync(payload);

        var typed = Assert.IsType<RefundSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundSucceeded, typed.Category);
        Assert.Equal("rf_1", typed.RefundReference);
        Assert.Equal("ch_1", typed.GatewayReference);
        Assert.Equal(25m, typed.Amount);
        Assert.True(typed.IsPartial);
    }

    [Fact]
    public async Task ParseWebhookAsync_RefundFailed_ReturnsTypedEvent()
    {
        var provider = Create();
        var payload = """
            {"type":"refund.failed","payload":{"id":"rf_2","chargeId":"ch_2","amountInCents":1000,"currency":"ZAR","failureCode":"window_expired"}}
            """;
        var evt = await provider.ParseWebhookAsync(payload);

        var typed = Assert.IsType<RefundFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundFailed, typed.Category);
        Assert.Equal("window_expired", typed.FailureCode);
    }

    [Fact]
    public async Task ParseWebhookAsync_PayoutCompleted_ReturnsTypedEvent()
    {
        var provider = Create();
        var payload = """
            {"type":"payout.completed","payload":{"id":"po_1","amountInCents":250000,"currency":"ZAR","bankAccountId":"ba_x"}}
            """;
        var evt = await provider.ParseWebhookAsync(payload);

        var typed = Assert.IsType<PayoutCompletedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutCompleted, typed.Category);
        Assert.Equal("po_1", typed.PayoutReference);
        Assert.Equal(2500m, typed.Amount);
        Assert.Equal("ba_x", typed.DestinationToken);
    }

    [Fact]
    public async Task ParseWebhookAsync_PayoutFailed_ReturnsTypedEvent()
    {
        var provider = Create();
        var payload = """
            {"type":"payout.failed","payload":{"id":"po_2","amountInCents":100000,"currency":"ZAR","failureCode":"invalid_account"}}
            """;
        var evt = await provider.ParseWebhookAsync(payload);

        var typed = Assert.IsType<PayoutFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutFailed, typed.Category);
        Assert.Equal("invalid_account", typed.FailureCode);
    }
}
