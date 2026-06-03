// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Bhengu.Finance.Payments.Flutterwave.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Flutterwave;

public class FlutterwaveTypedWebhookTests
{
    private static FlutterwavePaymentProvider Create() =>
        new(
            new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new FlutterwaveOptions { SecretKey = "FLWSECK_TEST", WebhookSecret = "secret" }),
            NullLogger<FlutterwavePaymentProvider>.Instance);

    [Fact]
    public void Capabilities_Include_TypedWebhooks()
    {
        var provider = Create();
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.TypedWebhooks));
    }

    [Fact]
    public async Task ParseWebhookAsync_ChargeCompletedSuccessful_ReturnsChargeSucceededEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"charge.completed","data":{"id":1,"tx_ref":"tx_99","status":"successful","amount":1000,"currency":"NGN","customer":{"id":1,"email":"buyer@example.com"},"card":{"token":"flw-tok_a","first_6digits":"411111","last_4digits":"1111","type":"VISA"}}}
            """);

        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal("tx_99", typed.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, typed.Status);
        Assert.Equal(1000m, typed.Amount);
        Assert.Equal("NGN", typed.Currency);
        Assert.Equal("buyer@example.com", typed.CustomerId);
        Assert.Equal("flw-tok_a", typed.PaymentMethodToken);
    }

    [Fact]
    public async Task ParseWebhookAsync_ChargeCompletedFailed_ReturnsChargeFailedEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"charge.completed","data":{"id":1,"tx_ref":"tx_failed","status":"failed","amount":500,"currency":"NGN","processor_response":"insufficient_funds","narration":"Insufficient funds"}}
            """);

        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal(PaymentStatus.Failed, typed.Status);
        Assert.Equal(500m, typed.Amount);
        Assert.Equal("insufficient_funds", typed.FailureCode);
    }

    [Fact]
    public async Task ParseWebhookAsync_TransferCompletedSuccessful_ReturnsPayoutCompletedEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"transfer.completed","data":{"id":1,"reference":"transfer-1","status":"SUCCESSFUL","amount":2000,"currency":"NGN","account_number":"0690000040"}}
            """);

        var typed = Assert.IsType<PayoutCompletedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutCompleted, typed.Category);
        Assert.Equal("transfer-1", typed.PayoutReference);
        Assert.Equal(2000m, typed.Amount);
        Assert.Equal("0690000040", typed.DestinationToken);
    }

    [Fact]
    public async Task ParseWebhookAsync_TransferCompletedFailed_ReturnsPayoutFailedEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"transfer.completed","data":{"id":1,"reference":"transfer-1","status":"FAILED","amount":2000,"currency":"NGN","complete_message":"BENEFICIARY_BANK_UNREACHABLE","narration":"unreachable"}}
            """);

        var typed = Assert.IsType<PayoutFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutFailed, typed.Category);
        Assert.Equal(PaymentStatus.Failed, typed.Status);
        Assert.Equal("BENEFICIARY_BANK_UNREACHABLE", typed.FailureCode);
    }

    [Fact]
    public async Task ParseWebhookAsync_SubscriptionCancelled_ReturnsSubscriptionCancelledEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"subscription.cancelled","data":{"id":1,"tx_ref":"sub-9","status":"cancelled"}}
            """);

        var typed = Assert.IsType<SubscriptionCancelledEvent>(evt);
        Assert.Equal(WebhookEventCategory.SubscriptionCancelled, typed.Category);
        Assert.Equal("sub-9", typed.SubscriptionReference);
        Assert.Equal(PaymentStatus.Cancelled, typed.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_UnknownEventType_ReturnsBaseEventWithUnknownCategory()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"some.weird.event","data":{"tx_ref":"tx_x","status":"completed"}}
            """);

        Assert.NotNull(evt);
        Assert.IsType<WebhookEvent>(evt);
        Assert.Equal(WebhookEventCategory.Unknown, evt!.Category);
        Assert.Equal("tx_x", evt.GatewayReference);
    }

    [Fact]
    public async Task ParseWebhookAsync_TransferFailedEvent_ReturnsPayoutFailedEvent()
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync("""
            {"event":"transfer.failed","data":{"id":1,"reference":"transfer-2","status":"FAILED","amount":1000,"currency":"NGN"}}
            """);

        var typed = Assert.IsType<PayoutFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutFailed, typed.Category);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create();
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_WhenReferenceMissing()
    {
        var provider = Create();
        Assert.Null(await provider.ParseWebhookAsync("""{"event":"charge.completed","data":{"status":"successful"}}"""));
    }
}
