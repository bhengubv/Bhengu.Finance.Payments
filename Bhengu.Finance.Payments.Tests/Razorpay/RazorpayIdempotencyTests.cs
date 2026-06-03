// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Bhengu.Finance.Payments.Razorpay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Razorpay;

/// <summary>
/// Verifies the <c>X-Razorpay-IdempotencyKey</c> header is sent on every POST endpoint that
/// honours idempotency, and is omitted (not sent) when the caller doesn't supply a key.
/// </summary>
public class RazorpayIdempotencyTests
{
    private static RazorpayPaymentProvider CreatePayment(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new RazorpayOptions
            {
                KeyId = "rzp_test_x",
                KeySecret = "secret_x",
                WebhookSecret = "wsec",
                RazorpayXAccountNumber = "2323230099089860"
            }),
            NullLogger<RazorpayPaymentProvider>.Instance);

    [Fact]
    public async Task ProcessPayment_SendsIdempotencyKey_WhenSupplied()
    {
        string? header = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            header = req.Headers.TryGetValues("X-Razorpay-IdempotencyKey", out var v) ? string.Join(",", v) : null;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"pay_x","entity":"payment","amount":10000,"currency":"INR","status":"captured"}""");
        });
        var provider = CreatePayment(handler);
        await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "pay_x",
            Amount = 100m,
            Currency = "INR",
            Description = "x",
            IdempotencyKey = "idem-payment-1"
        });
        Assert.Equal("idem-payment-1", header);
    }

    [Fact]
    public async Task ProcessPayment_OmitsIdempotencyHeader_WhenNotSupplied()
    {
        var sent = false;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            sent = req.Headers.Contains("X-Razorpay-IdempotencyKey");
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"pay_x","entity":"payment","amount":10000,"currency":"INR","status":"captured"}""");
        });
        var provider = CreatePayment(handler);
        await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "pay_x",
            Amount = 100m,
            Currency = "INR",
            Description = "x"
        });
        Assert.False(sent);
    }

    [Fact]
    public async Task ProcessOrder_SendsIdempotencyKey_WhenSupplied()
    {
        string? header = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            header = req.Headers.TryGetValues("X-Razorpay-IdempotencyKey", out var v) ? string.Join(",", v) : null;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"order_x","entity":"order","amount":10000,"currency":"INR","status":"created"}""");
        });
        var provider = CreatePayment(handler);
        await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "_unused",
            Amount = 100m,
            Currency = "INR",
            Description = "x",
            IdempotencyKey = "idem-order-1",
            Metadata = new Dictionary<string, string> { ["flow"] = "order" }
        });
        Assert.Equal("idem-order-1", header);
    }

    [Fact]
    public async Task ProcessRefund_SendsIdempotencyKey_WhenSupplied()
    {
        string? header = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            header = req.Headers.TryGetValues("X-Razorpay-IdempotencyKey", out var v) ? string.Join(",", v) : null;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"rfnd_x","entity":"refund","amount":5000,"currency":"INR","status":"processed"}""");
        });
        var provider = CreatePayment(handler);
        await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "pay_x",
            Amount = 50m,
            Reason = "x",
            IdempotencyKey = "idem-refund-1"
        });
        Assert.Equal("idem-refund-1", header);
    }

    [Fact]
    public async Task ProcessPayout_SendsIdempotencyKey_WhenSupplied()
    {
        string? header = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            header = req.Headers.TryGetValues("X-Razorpay-IdempotencyKey", out var v) ? string.Join(",", v) : null;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"pout_x","entity":"payout","amount":50000,"currency":"INR","status":"processed"}""");
        });
        var provider = CreatePayment(handler);
        await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "fa_x",
            Amount = 500m,
            Currency = "INR",
            Description = "x",
            IdempotencyKey = "idem-payout-1"
        });
        Assert.Equal("idem-payout-1", header);
    }
}
