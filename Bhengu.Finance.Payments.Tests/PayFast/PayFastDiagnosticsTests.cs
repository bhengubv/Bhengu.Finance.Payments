// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Bhengu.Finance.Payments.PayFast.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayFast;

/// <summary>OTel counter assertions for the PayFast provider family.</summary>
public class PayFastDiagnosticsTests
{
    [Fact]
    public async Task ProcessPaymentAsync_IncrementsChargesTotal()
    {
        using var recorder = new DiagnosticsCounterRecorder();
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"code":200,"status":"success","data":{"message":true,"pf_payment_id":"PF-DIAG","response":"APPROVED"}}
            """));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://sandbox.payfast.co.za/") };
        var provider = new PayFastPaymentProvider(
            http,
            Options.Create(new PayFastOptions { MerchantId = "10000100", Passphrase = "p", UseSandbox = true }),
            NullLogger<PayFastPaymentProvider>.Instance);

        await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "f4c8e1d2-1234-5678-9abc-def012345678",
            Amount = 10m,
            Currency = "ZAR",
            Description = "diag"
        });

        // Global meter: parallel same-provider charge tests can add to this — assert >= 1, not == 1.
        Assert.True(recorder.CounterTotalFor("bhengu_payments_charges_total", "payfast") >= 1);
    }
}
