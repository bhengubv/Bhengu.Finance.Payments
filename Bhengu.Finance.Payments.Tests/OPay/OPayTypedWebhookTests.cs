// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.OPay.Configuration;
using Bhengu.Finance.Payments.OPay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.OPay;

public class OPayTypedWebhookTests
{
    private static OPayPaymentProvider Create() => new(
        new HttpClient(new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"))),
        Options.Create(new OPayOptions
        {
            PublicKey = "p",
            SecretKey = "s",
            MerchantId = "m",
            Country = "NG",
            CallbackUrl = "https://x",
            ReturnUrl = "https://y"
        }),
        NullLogger<OPayPaymentProvider>.Instance);

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeSucceededEvent_ForTransactionSuccess()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"type":"transaction.success","payload":{"reference":"R-1","amount":{"total":250000,"currency":"NGN"},"userId":"U-1","payMethod":"card"}}
            """);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(2500m, typed.Amount);
        Assert.Equal("U-1", typed.CustomerId);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeFailedEvent_ForTransactionFailed()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"type":"transaction.failed","payload":{"reference":"R-2","amount":{"total":100000,"currency":"NGN"},"status":"failed","failureReason":"insufficient_funds"}}
            """);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal("insufficient_funds", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsRefundSucceededEvent_ForRefundSuccess()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"type":"refund.success","payload":{"reference":"R-3","amount":{"total":50000,"currency":"NGN"},"refundId":"RF-1"}}
            """);
        var typed = Assert.IsType<RefundSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundSucceeded, typed.Category);
        Assert.Equal("RF-1", typed.RefundReference);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsPayoutCompletedEvent_ForPayoutSuccess()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"type":"payout.success","payload":{"reference":"R-4","amount":{"total":1000000,"currency":"NGN"},"receiverId":"USR-9000"}}
            """);
        var typed = Assert.IsType<PayoutCompletedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutCompleted, typed.Category);
        Assert.Equal("USR-9000", typed.DestinationToken);
        Assert.Equal(10000m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsUnknownCategory_ForUnknownEvent()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"type":"some.random","payload":{"reference":"X"}}
            """);
        Assert.NotNull(evt);
        Assert.Equal(WebhookEventCategory.Unknown, evt!.Category);
    }
}
