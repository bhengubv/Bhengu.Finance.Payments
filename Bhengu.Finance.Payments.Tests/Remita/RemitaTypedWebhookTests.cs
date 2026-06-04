// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Remita.Configuration;
using Bhengu.Finance.Payments.Remita.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Remita;

public class RemitaTypedWebhookTests
{
    private static RemitaPaymentProvider Create() => new(
        new HttpClient(new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"))),
        Options.Create(new RemitaOptions { MerchantId = "M", ServiceTypeId = "S", ApiKey = "K" }),
        NullLogger<RemitaPaymentProvider>.Instance);

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeSucceededEvent_OnSuccessStatus()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"rrr":"RRR-1","status":"00","amount":2500.00,"currency":"NGN"}
            """);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(2500m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeFailedEvent_OnFailedStatus()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"rrr":"RRR-2","status":"020","amount":100.00,"currency":"NGN","message":"declined"}
            """);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal("declined", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsMandateActivatedEvent_OnMandateActivated()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"rrr":"R-1","notificationType":"mandate.activated","mandateId":"MAN-1","amount":5000.00,"currency":"NGN"}
            """);
        var typed = Assert.IsType<MandateActivatedEvent>(evt);
        Assert.Equal(WebhookEventCategory.MandateActivated, typed.Category);
        Assert.Equal("MAN-1", typed.MandateReference);
        Assert.Equal(5000m, typed.AmountLimit);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsMandateCancelledEvent_OnMandateCancelled()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"rrr":"R-2","notificationType":"mandate.cancelled","mandateId":"MAN-2","cancellationReason":"payer"}
            """);
        var typed = Assert.IsType<MandateCancelledEvent>(evt);
        Assert.Equal(WebhookEventCategory.MandateCancelled, typed.Category);
        Assert.Equal("MAN-2", typed.MandateReference);
        Assert.Equal("payer", typed.CancellationReason);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsPayoutCompletedEvent_OnTransferSuccessful()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"rrr":"R-3","notificationType":"transfer.successful","transRef":"T-1","amount":1000.00,"currency":"NGN","creditAccount":"0123456789"}
            """);
        var typed = Assert.IsType<PayoutCompletedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutCompleted, typed.Category);
        Assert.Equal("T-1", typed.PayoutReference);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownEvent()
    {
        var evt = await Create().ParseWebhookAsync("""
            {"rrr":"R-X","status":"unknown_code"}
            """);
        Assert.Null(evt);
    }
}
