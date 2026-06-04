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

/// <summary>OTel counter assertions for the Razorpay provider family.</summary>
public class RazorpayDiagnosticsTests
{
    [Fact]
    public async Task ProcessPaymentAsync_IncrementsChargesTotal()
    {
        using var recorder = new DiagnosticsCounterRecorder();
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"id":"pay_diag","entity":"payment","amount":1000,"currency":"INR","status":"captured"}
            """));
        var provider = new RazorpayPaymentProvider(
            new HttpClient(handler),
            Options.Create(new RazorpayOptions { KeyId = "rzp_test", KeySecret = "secret", WebhookSecret = "w" }),
            NullLogger<RazorpayPaymentProvider>.Instance);

        await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "pay_x",
            Amount = 10m,
            Currency = "INR",
            Description = "diag"
        });

        Assert.Equal(1, recorder.CounterTotalFor("bhengu_payments_charges_total", "razorpay"));
        Assert.True(recorder.HistogramObservationsFor("bhengu_payments_charge_duration_ms", "razorpay") >= 1);
    }
}
