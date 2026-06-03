// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Stitch.Configuration;
using Bhengu.Finance.Payments.Stitch.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Stitch;

/// <summary>
/// Verifies that <see cref="StitchPaymentProvider.ParseWebhookAsync"/> upgrades each known Stitch
/// event into the right strongly-typed <see cref="WebhookEvent"/> sub-record. Particularly important
/// for the DebiCheck mandate lifecycle (paymentInitiation.completed → MandateActivatedEvent) since
/// consumers depend on the typed event to know when a mandate is chargeable.
/// </summary>
public class StitchTypedWebhookTests
{
    private static StitchPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new StitchOptions
            {
                ClientId = "c",
                ApiKey = "k",
                WebhookSecret = "wsec",
                BeneficiaryAccountNumber = "123",
                BeneficiaryBankId = "fnb",
                BeneficiaryName = "x"
            }),
            NullLogger<StitchPaymentProvider>.Instance);

    [Fact]
    public async Task ParseWebhook_PaymentInitiationRequestCompleted_ReturnsChargeSucceededEvent()
    {
        var json = """{"eventType":"paymentInitiationRequest.completed","data":{"id":"pir_001","status":"completed","amount":{"quantity":"150.00","currency":"ZAR"},"payer":{"reference":"cust_001"}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal("pir_001", typed.GatewayReference);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(150m, typed.Amount);
        Assert.Equal("ZAR", typed.Currency);
        Assert.Equal("cust_001", typed.CustomerId);
    }

    [Fact]
    public async Task ParseWebhook_PaymentInitiationRequestPending_ReturnsChargePendingEvent()
    {
        var json = """{"eventType":"paymentInitiationRequest.pending","data":{"id":"pir_002","status":"pending","amount":{"quantity":"75.00","currency":"ZAR"}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargePendingEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargePending, typed.Category);
        Assert.Equal(PaymentStatus.Pending, typed.Status);
        Assert.Equal(75m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhook_PaymentInitiationRequestFailed_ReturnsChargeFailedEvent()
    {
        var json = """{"eventType":"paymentInitiationRequest.failed","data":{"id":"pir_003","status":"insufficient_funds","amount":{"quantity":"99.00","currency":"ZAR"}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal("insufficient_funds", typed.FailureCode);
    }

    [Fact]
    public async Task ParseWebhook_PaymentInitiationCompleted_ReturnsMandateActivatedEvent()
    {
        // Stitch fires paymentInitiation.completed once the payer's bank confirms the DebiCheck
        // authorisation — this is the moment the mandate becomes chargeable.
        var json = """{"eventType":"paymentInitiation.completed","data":{"id":"pi_mand_1","status":"active","amount":{"quantity":"2500.00","currency":"ZAR"}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<MandateActivatedEvent>(evt);
        Assert.Equal(WebhookEventCategory.MandateActivated, typed.Category);
        Assert.Equal("pi_mand_1", typed.MandateReference);
        Assert.Equal(2500m, typed.AmountLimit);
        Assert.Equal("ZAR", typed.Currency);
    }

    [Fact]
    public async Task ParseWebhook_PaymentInitiationCancelled_ReturnsMandateCancelledEvent()
    {
        var json = """{"eventType":"paymentInitiation.cancelled","data":{"id":"pi_mand_1","status":"cancelled"}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<MandateCancelledEvent>(evt);
        Assert.Equal(WebhookEventCategory.MandateCancelled, typed.Category);
        Assert.Equal("pi_mand_1", typed.MandateReference);
        Assert.Equal("cancelled", typed.CancellationReason);
    }

    [Fact]
    public async Task ParseWebhook_RefundCompleted_ReturnsRefundSucceededEvent()
    {
        var json = """{"eventType":"refund.completed","data":{"id":"rf_001","status":"completed","amount":{"quantity":"50.00","currency":"ZAR"}}}""";
        var evt = await Create().ParseWebhookAsync(json);
        var typed = Assert.IsType<RefundSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundSucceeded, typed.Category);
        Assert.Equal("rf_001", typed.RefundReference);
        Assert.Equal(50m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhook_UnknownEvent_ReturnsNull()
    {
        var json = """{"eventType":"some.unrelated.event","data":{"id":"x"}}""";
        Assert.Null(await Create().ParseWebhookAsync(json));
    }
}
