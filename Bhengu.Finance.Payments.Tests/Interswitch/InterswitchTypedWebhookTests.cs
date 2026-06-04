// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Interswitch.Configuration;
using Bhengu.Finance.Payments.Interswitch.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Interswitch;

public class InterswitchTypedWebhookTests
{
    private static InterswitchPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) =>
                StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"))),
            Options.Create(new InterswitchOptions
            {
                ClientId = "id",
                ClientSecret = "secret",
                WebhookSecret = "wh"
            }),
            NullLogger<InterswitchPaymentProvider>.Instance);

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeSucceededEvent_ForPaymentSuccessful()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"eventType":"payment.successful","data":{"transactionRef":"ISW-1","amount":250000,"currency":"NGN","customerId":"CUS-001","paymentMethod":"card"}}
            """);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(2500m, typed.Amount);
        Assert.Equal("NGN", typed.Currency);
        Assert.Equal("CUS-001", typed.CustomerId);
        Assert.Equal(PaymentStatus.Completed, typed.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeFailedEvent_ForPaymentFailed()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"eventType":"payment.failed","data":{"transactionRef":"ISW-2","amount":50000,"currency":"NGN","status":"05","responseDescription":"do not honor"}}
            """);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal("do not honor", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsRefundSucceededEvent_ForRefundProcessed()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"eventType":"refund.successful","data":{"transactionRef":"ISW-3","amount":10000,"currency":"NGN","refundReference":"RF-1"}}
            """);
        var typed = Assert.IsType<RefundSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundSucceeded, typed.Category);
        Assert.Equal("RF-1", typed.RefundReference);
        Assert.Equal(100m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsPayoutCompletedEvent_ForDisbursementSuccessful()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"eventType":"disbursement.successful","data":{"transactionRef":"DISB-1","amount":1000000,"currency":"NGN","beneficiaryAccountNumber":"0123456789"}}
            """);
        var typed = Assert.IsType<PayoutCompletedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutCompleted, typed.Category);
        Assert.Equal("0123456789", typed.DestinationToken);
        Assert.Equal(10000m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownEvent()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"eventType":"some.random.event","data":{"transactionRef":"X"}}
            """);
        Assert.Null(evt);
    }
}
