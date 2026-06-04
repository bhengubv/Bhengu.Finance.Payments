// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Fawry.Configuration;
using Bhengu.Finance.Payments.Fawry.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Fawry;

public class FawryTypedWebhookTests
{
    private static FawryPaymentProvider Create() => new(
        new HttpClient(new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"))),
        Options.Create(new FawryOptions { MerchantCode = "MC", SecurityKey = "SK" }),
        NullLogger<FawryPaymentProvider>.Instance);

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeSucceededEvent_OnPaid()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"fawryRefNumber":"F-1","orderStatus":"PAID","paymentAmount":"2500.00","currencyCode":"EGP","customerProfileId":"CUS-1","paymentMethod":"CARD"}
            """);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(2500m, typed.Amount);
        Assert.Equal("CUS-1", typed.CustomerId);
        Assert.Equal("CARD", typed.PaymentMethodToken);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargePendingEvent_OnNew()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"fawryRefNumber":"F-2","orderStatus":"NEW","paymentAmount":"100.00","currencyCode":"EGP"}
            """);
        var typed = Assert.IsType<ChargePendingEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargePending, typed.Category);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeFailedEvent_OnExpired()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"fawryRefNumber":"F-3","orderStatus":"EXPIRED","paymentAmount":"50.00","currencyCode":"EGP","failureErrorCode":"TIMEOUT","failureReason":"window passed"}
            """);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal("window passed", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsRefundSucceededEvent_OnRefunded()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"fawryRefNumber":"F-4","orderStatus":"REFUNDED","paymentAmount":"75.00","currencyCode":"EGP","refundReference":"RF-9"}
            """);
        var typed = Assert.IsType<RefundSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundSucceeded, typed.Category);
        Assert.Equal("RF-9", typed.RefundReference);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsSettlementCompletedEvent_OnSettled()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"fawryRefNumber":"S-1","orderStatus":"SETTLED","paymentAmount":"10000.00","currencyCode":"EGP"}
            """);
        var typed = Assert.IsType<SettlementCompletedEvent>(evt);
        Assert.Equal(WebhookEventCategory.SettlementCompleted, typed.Category);
        Assert.Equal(10000m, typed.NetAmount);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownOrderStatus()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"fawryRefNumber":"X","orderStatus":"SOMETHING_WEIRD"}
            """);
        Assert.Null(evt);
    }
}
