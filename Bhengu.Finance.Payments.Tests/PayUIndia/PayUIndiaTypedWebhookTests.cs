// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.PayUIndia.Configuration;
using Bhengu.Finance.Payments.PayUIndia.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayUIndia;

public class PayUIndiaTypedWebhookTests
{
    private static PayUIndiaPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new PayUIndiaOptions
            {
                MerchantKey = "gtKFFx",
                Salt = "eCwWELxi",
                SuccessUrl = "https://x/s",
                FailureUrl = "https://x/f"
            }),
            NullLogger<PayUIndiaPaymentProvider>.Instance);

    [Fact]
    public async Task ParseWebhook_Success_ReturnsChargeSucceededEvent()
    {
        var evt = await Create().ParseWebhookAsync("status=success&txnid=txn1&mihpayid=403&amount=100.00");
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal("txn1", typed.GatewayReference);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(100m, typed.Amount);
        Assert.Equal("403", typed.PaymentMethodToken);
    }

    [Fact]
    public async Task ParseWebhook_Failure_ReturnsChargeFailedEvent()
    {
        var evt = await Create().ParseWebhookAsync("status=failure&txnid=txn2&amount=100.00&error_Message=insufficient_funds");
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal("failure", typed.FailureCode);
        Assert.Equal("insufficient_funds", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhook_Pending_ReturnsChargePendingEvent()
    {
        var evt = await Create().ParseWebhookAsync("status=pending&txnid=txn3&amount=50.00");
        var typed = Assert.IsType<ChargePendingEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargePending, typed.Category);
        Assert.Equal(PaymentStatus.Pending, typed.Status);
    }

    [Fact]
    public async Task ParseWebhook_Refunded_ReturnsRefundSucceededEvent()
    {
        var evt = await Create().ParseWebhookAsync("status=refunded&txnid=txn4&mihpayid=R1&amount=25.00");
        var typed = Assert.IsType<RefundSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundSucceeded, typed.Category);
        Assert.Equal("R1", typed.RefundReference);
        Assert.Equal(25m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhook_JsonPayload_ParsedCorrectly()
    {
        var evt = await Create().ParseWebhookAsync("""{"status":"success","txnid":"txn5","mihpayid":"403","amount":"75.00"}""");
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal("txn5", typed.GatewayReference);
        Assert.Equal(75m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhook_UnknownStatus_ReturnsBaseEvent()
    {
        var evt = await Create().ParseWebhookAsync("status=enroute&txnid=txn6");
        Assert.NotNull(evt);
        Assert.IsType<WebhookEvent>(evt);
        Assert.Equal("txn6", evt!.GatewayReference);
    }
}
