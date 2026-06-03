// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.TymeBank.Configuration;
using Bhengu.Finance.Payments.TymeBank.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.TymeBank;

/// <summary>
/// Verifies that <see cref="TymeBankPaymentProvider.ParseWebhookAsync"/> upgrades each known
/// TymeBank event into the right strongly-typed <see cref="WebhookEvent"/> sub-record.
/// Particularly important for the debit-order mandate lifecycle since subscription billing depends
/// on knowing precisely when mandates activate, cancel, or fail.
/// </summary>
public class TymeBankTypedWebhookTests
{
    private static TymeBankPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new TymeBankOptions
            {
                ClientId = "c",
                ClientSecret = "s",
                WebhookSecret = "wsec"
            }),
            NullLogger<TymeBankPaymentProvider>.Instance);

    [Fact]
    public async Task ParseWebhook_PaymentCompleted_ReturnsChargeSucceededEvent()
    {
        var json = """{"event_type":"payment.completed","data":{"payment_id":"pay_t1","status":"completed","amount":"250.00","currency":"ZAR","customer_reference":"cust_1"}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal("pay_t1", typed.GatewayReference);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(250m, typed.Amount);
        Assert.Equal("ZAR", typed.Currency);
        Assert.Equal("cust_1", typed.CustomerId);
    }

    [Fact]
    public async Task ParseWebhook_PaymentFailed_ReturnsChargeFailedEvent()
    {
        var json = """{"event_type":"payment.failed","data":{"payment_id":"pay_t2","status":"failed","amount":"100.00","currency":"ZAR","failure_code":"insufficient_funds","failure_reason":"Insufficient funds"}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal("insufficient_funds", typed.FailureCode);
        Assert.Equal("Insufficient funds", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhook_MandateActivated_ReturnsMandateActivatedEvent()
    {
        // TymeBank fires mandate.activated once the payer authorises in-app — at that point the
        // mandate is chargeable via ChargeMandateAsync.
        var json = """{"event_type":"mandate.activated","data":{"mandate_id":"mand_t1","status":"active","amount_limit":"2500.00","currency":"ZAR"}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<MandateActivatedEvent>(evt);
        Assert.Equal(WebhookEventCategory.MandateActivated, typed.Category);
        Assert.Equal("mand_t1", typed.MandateReference);
        Assert.Equal(2500m, typed.AmountLimit);
        Assert.Equal("ZAR", typed.Currency);
    }

    [Fact]
    public async Task ParseWebhook_MandateCancelled_ReturnsMandateCancelledEvent()
    {
        var json = """{"event_type":"mandate.cancelled","data":{"mandate_id":"mand_t1","status":"cancelled"}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<MandateCancelledEvent>(evt);
        Assert.Equal(WebhookEventCategory.MandateCancelled, typed.Category);
        Assert.Equal("mand_t1", typed.MandateReference);
        Assert.Equal("cancelled", typed.CancellationReason);
    }

    [Fact]
    public async Task ParseWebhook_MandateDebitCompleted_ReturnsChargeSucceededEvent()
    {
        // Debits pulled against a mandate fire their own succeeded events. Reconciliation needs the
        // typed event for charge-against-mandate to be classified the same as a one-off payment.
        var json = """{"event_type":"mandate.debit.completed","data":{"debit_id":"deb_1","mandate_id":"mand_t1","status":"completed","amount":"199.00","currency":"ZAR"}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal("deb_1", typed.GatewayReference);
        Assert.Equal(199m, typed.Amount);
        Assert.Equal("mand_t1", typed.PaymentMethodToken);
    }

    [Fact]
    public async Task ParseWebhook_MandateDebitFailed_ReturnsChargeFailedEvent()
    {
        var json = """{"event_type":"mandate.debit.failed","data":{"debit_id":"deb_2","mandate_id":"mand_t1","status":"failed","amount":"199.00","currency":"ZAR","failure_code":"insufficient_funds"}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal("insufficient_funds", typed.FailureCode);
        Assert.Equal(199m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhook_PayoutCompleted_ReturnsPayoutCompletedEvent()
    {
        var json = """{"event_type":"payout.completed","data":{"payout_id":"po_1","status":"completed","amount":"500.00","currency":"ZAR"}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<PayoutCompletedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutCompleted, typed.Category);
        Assert.Equal(500m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhook_UnknownEvent_ReturnsNull()
    {
        var json = """{"event_type":"unknown.event","data":{"payment_id":"x"}}""";
        Assert.Null(await Create().ParseWebhookAsync(json));
    }
}
