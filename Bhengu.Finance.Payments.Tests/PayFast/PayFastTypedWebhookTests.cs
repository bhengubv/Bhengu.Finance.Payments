// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Bhengu.Finance.Payments.PayFast.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayFast;

public class PayFastTypedWebhookTests
{
    private static PayFastPaymentProvider Create()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)))
        {
            BaseAddress = new Uri("https://sandbox.payfast.co.za/")
        };
        return new PayFastPaymentProvider(
            http,
            Options.Create(new PayFastOptions { MerchantId = "10000100", Passphrase = "p", UseSandbox = true }),
            NullLogger<PayFastPaymentProvider>.Instance);
    }

    [Fact]
    public async Task ParseWebhookAsync_CompleteWithoutToken_ReturnsChargeSucceeded()
    {
        var provider = Create();
        var payload = "m_payment_id=mp1&pf_payment_id=PF-1&payment_status=COMPLETE&amount_gross=99.99&amount_currency=ZAR&custom_str1=cust-1";
        var evt = await provider.ParseWebhookAsync(payload);

        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal("PF-1", typed.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, typed.Status);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(99.99m, typed.Amount);
        Assert.Equal("ZAR", typed.Currency);
        Assert.Equal("cust-1", typed.CustomerId);
    }

    [Fact]
    public async Task ParseWebhookAsync_Failed_ReturnsChargeFailed()
    {
        var provider = Create();
        var payload = "m_payment_id=mp2&pf_payment_id=PF-2&payment_status=FAILED&amount_gross=50&amount_currency=ZAR&reason_code=insufficient_funds&reason=Bank+declined";
        var evt = await provider.ParseWebhookAsync(payload);

        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal(PaymentStatus.Failed, typed.Status);
        Assert.Equal("insufficient_funds", typed.FailureCode);
        Assert.Equal("Bank declined", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhookAsync_Pending_ReturnsChargePending()
    {
        var provider = Create();
        var payload = "m_payment_id=mp3&pf_payment_id=PF-3&payment_status=PENDING&amount_gross=10&amount_currency=ZAR";
        var evt = await provider.ParseWebhookAsync(payload);

        var typed = Assert.IsType<ChargePendingEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargePending, typed.Category);
        Assert.Equal(PaymentStatus.Pending, typed.Status);
        Assert.Equal(10m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_CompleteWithToken_FirstTime_ReturnsSubscriptionCreated()
    {
        var provider = Create();
        // Use a unique token to avoid the static dedup cache from prior tests.
        var token = $"tok-{Guid.NewGuid():N}";
        var payload = $"m_payment_id=mp4&pf_payment_id=PF-4&payment_status=COMPLETE&token={token}&custom_str1=cust-3&custom_str2=plan-z&amount_gross=99&amount_currency=ZAR";
        var evt = await provider.ParseWebhookAsync(payload);

        var typed = Assert.IsType<SubscriptionCreatedEvent>(evt);
        Assert.Equal(WebhookEventCategory.SubscriptionCreated, typed.Category);
        Assert.Equal(token, typed.SubscriptionReference);
        Assert.Equal("plan-z", typed.PlanReference);
        Assert.Equal("cust-3", typed.CustomerId);
    }

    [Fact]
    public async Task ParseWebhookAsync_CompleteWithToken_SecondTime_ReturnsSubscriptionRenewed()
    {
        var provider = Create();
        var token = $"tok-{Guid.NewGuid():N}";
        // First IPN: registers token.
        var first = $"m_payment_id=a&pf_payment_id=PF-A&payment_status=COMPLETE&token={token}&amount_gross=50&amount_currency=ZAR";
        await provider.ParseWebhookAsync(first);

        // Second IPN with same token → SubscriptionRenewedEvent.
        var second = $"m_payment_id=b&pf_payment_id=PF-B&payment_status=COMPLETE&token={token}&amount_gross=50&amount_currency=ZAR";
        var evt = await provider.ParseWebhookAsync(second);

        var typed = Assert.IsType<SubscriptionRenewedEvent>(evt);
        Assert.Equal(WebhookEventCategory.SubscriptionRenewed, typed.Category);
        Assert.Equal(token, typed.SubscriptionReference);
        Assert.Equal(50m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_CancelledWithToken_ReturnsSubscriptionCancelled()
    {
        var provider = Create();
        var token = $"tok-{Guid.NewGuid():N}";
        var payload = $"m_payment_id=c&pf_payment_id=PF-C&payment_status=CANCELLED&token={token}&reason=user_requested";
        var evt = await provider.ParseWebhookAsync(payload);

        var typed = Assert.IsType<SubscriptionCancelledEvent>(evt);
        Assert.Equal(WebhookEventCategory.SubscriptionCancelled, typed.Category);
        Assert.Equal(token, typed.SubscriptionReference);
        Assert.Equal("user_requested", typed.CancellationReason);
    }

    [Fact]
    public async Task ParseWebhookAsync_PreservesRawPayload()
    {
        var provider = Create();
        var payload = "m_payment_id=mp5&pf_payment_id=PF-5&payment_status=COMPLETE&amount_gross=1&amount_currency=ZAR&extra=value";
        var evt = await provider.ParseWebhookAsync(payload);

        Assert.NotNull(evt);
        Assert.NotNull(evt!.RawPayload);
        Assert.Equal("value", evt.RawPayload!["extra"]);
    }

    [Fact]
    public async Task ParseWebhookAsync_UnknownStatus_ReturnsUnknownCategory()
    {
        var provider = Create();
        var payload = "m_payment_id=mp6&pf_payment_id=PF-6&payment_status=UNKNOWNSTATE&amount_gross=1";
        var evt = await provider.ParseWebhookAsync(payload);

        Assert.NotNull(evt);
        Assert.Equal(WebhookEventCategory.Unknown, evt!.Category);
    }
}
