// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Moniepoint.Configuration;
using Bhengu.Finance.Payments.Moniepoint.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Moniepoint;

public class MoniepointTypedWebhookTests
{
    private static MoniepointPaymentProvider Create() => new(
        new HttpClient(new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"))),
        Options.Create(new MoniepointOptions { ApiKey = "k", MerchantId = "m" }),
        NullLogger<MoniepointPaymentProvider>.Instance);

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeSucceededEvent_ForTransactionSuccessful()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"event":"transaction.successful","data":{"reference":"R-1","amount":2500,"currency":"NGN","customerEmail":"x@y","paymentMethod":"card","status":"successful"}}
            """);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(2500m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeFailedEvent_ForTransactionFailed()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"event":"transaction.failed","data":{"reference":"R-2","amount":100,"currency":"NGN","status":"failed","failureReason":"insufficient_funds"}}
            """);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal("insufficient_funds", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsRefundSucceededEvent_ForRefundProcessed()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"event":"refund.processed","data":{"reference":"R-3","amount":50,"currency":"NGN","refundReference":"RF-1"}}
            """);
        var typed = Assert.IsType<RefundSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundSucceeded, typed.Category);
        Assert.Equal("RF-1", typed.RefundReference);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsPayoutCompletedEvent_ForTransferSuccessful()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"event":"transfer.successful","data":{"reference":"T-1","amount":10000,"currency":"NGN","beneficiaryAccount":"0123456789"}}
            """);
        var typed = Assert.IsType<PayoutCompletedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutCompleted, typed.Category);
        Assert.Equal("0123456789", typed.DestinationToken);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownEvent()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"event":"unknown.thing","data":{"reference":"X"}}
            """);
        Assert.Null(evt);
    }
}
