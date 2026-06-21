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
        new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"))),
        Options.Create(new MoniepointOptions { ApiKey = "k", SecretKey = "s", ContractCode = "c" }),
        NullLogger<MoniepointPaymentProvider>.Instance);

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeSucceeded_ForSuccessfulTransaction()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"eventType":"SUCCESSFUL_TRANSACTION","eventData":{"transactionReference":"R-1","amountPaid":2500,"currencyCode":"NGN","customer":{"email":"x@y"},"paymentMethod":"CARD","paymentStatus":"PAID"}}
            """);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(2500m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeFailed_ForFailedTransaction()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"eventType":"FAILED_TRANSACTION","eventData":{"transactionReference":"R-2","amountPaid":100,"currencyCode":"NGN","paymentStatus":"FAILED"}}
            """);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsRefundSucceeded_ForSuccessfulRefund()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"eventType":"SUCCESSFUL_REFUND","eventData":{"transactionReference":"R-3","amount":50,"currencyCode":"NGN","refundReference":"RF-1"}}
            """);
        var typed = Assert.IsType<RefundSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundSucceeded, typed.Category);
        Assert.Equal("RF-1", typed.RefundReference);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsPayoutCompleted_ForSuccessfulDisbursement()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"eventType":"SUCCESSFUL_DISBURSEMENT","eventData":{"reference":"T-1","amount":10000,"currencyCode":"NGN","destinationAccountNumber":"0123456789"}}
            """);
        var typed = Assert.IsType<PayoutCompletedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutCompleted, typed.Category);
        Assert.Equal("0123456789", typed.DestinationToken);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownEvent() =>
        Assert.Null(await Create().ParseWebhookAsync("""
            {"eventType":"UNKNOWN_THING","eventData":{"transactionReference":"X"}}
            """));
}
