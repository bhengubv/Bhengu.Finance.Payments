// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Internals;
using Bhengu.Finance.Payments.Paystack.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paystack;

/// <summary>OTel counter assertions for the Paystack provider family.</summary>
public class PaystackDiagnosticsTests
{
    [Fact]
    public async Task ProcessPaymentAsync_IncrementsChargesTotal()
    {
        using var recorder = new DiagnosticsCounterRecorder();
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"status":true,"message":"ok","data":{"id":1,"reference":"ref_diag","status":"success","amount":10000,"currency":"NGN"}}
            """));
        var provider = new PaystackPaymentProvider(
            new HttpClient(handler),
            Options.Create(new PaystackOptions { SecretKey = "sk_test", DefaultEmail = "b@x.com" }),
            NullLogger<PaystackPaymentProvider>.Instance,
            new PaystackIdempotencyCache());

        await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "AUTH_x",
            Amount = 100m,
            Currency = "NGN",
            Description = "diag"
        });

        // Global meter: parallel same-provider charge tests can add to this — assert >= 1, not == 1.
        Assert.True(recorder.CounterTotalFor("bhengu_payments_charges_total", "paystack") >= 1);
        Assert.True(recorder.HistogramObservationsFor("bhengu_payments_charge_duration_ms", "paystack") >= 1);
    }
}
