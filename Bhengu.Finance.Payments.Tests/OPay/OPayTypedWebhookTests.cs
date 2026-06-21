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

    // OPay's real payment-notification callback (https://documentation.opaycheckout.com/payment-notifications-callbacks)
    // has a single envelope type "transaction-status"; outcome lives in payload.status, and a refund
    // is flagged by payload.refunded == true. amount is a scalar in minor units with a sibling currency.

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeSucceededEvent_ForSuccessStatus()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"type":"transaction-status","payload":{"reference":"R-1","amount":250000,"currency":"NGN","status":"SUCCESS","refunded":false,"userId":"U-1","instrumentType":"BankCard"}}
            """);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(2500m, typed.Amount);
        Assert.Equal("U-1", typed.CustomerId);
        Assert.Equal("BankCard", typed.PaymentMethodToken);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeFailedEvent_ForFailStatus()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"type":"transaction-status","payload":{"reference":"R-2","amount":100000,"currency":"NGN","status":"FAIL","refunded":false,"failureReason":"insufficient_funds"}}
            """);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal("insufficient_funds", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsRefundSucceededEvent_WhenRefundedTrue()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"type":"transaction-status","payload":{"reference":"R-3","amount":50000,"currency":"NGN","status":"SUCCESS","refunded":true,"transactionId":"TX-3"}}
            """);
        var typed = Assert.IsType<RefundSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundSucceeded, typed.Category);
        Assert.Equal("TX-3", typed.RefundReference);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsRefundFailedEvent_WhenRefundedTrueAndFail()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"type":"transaction-status","payload":{"reference":"R-3b","amount":50000,"currency":"NGN","status":"FAIL","refunded":true,"failureReason":"refund_rejected"}}
            """);
        var typed = Assert.IsType<RefundFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundFailed, typed.Category);
        Assert.Equal("refund_rejected", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsPending_ForInitialStatus()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"type":"transaction-status","payload":{"reference":"X","status":"INITIAL"}}
            """);
        Assert.NotNull(evt);
        Assert.Equal(WebhookEventCategory.ChargePending, evt!.Category);
    }
}
