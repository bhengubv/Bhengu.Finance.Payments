// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Bhengu.Finance.Payments.Flutterwave.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Flutterwave;

/// <summary>OTel counter assertions for the Flutterwave provider family.</summary>
public class FlutterwaveDiagnosticsTests
{
    [Fact]
    public async Task ProcessPaymentAsync_IncrementsChargesTotal()
    {
        using var recorder = new DiagnosticsCounterRecorder();
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"status":"success","data":{"link":"https://flutterwave.test/x"}}
            """));
        var provider = new FlutterwavePaymentProvider(
            new HttpClient(handler),
            Options.Create(new FlutterwaveOptions { SecretKey = "FLWSECK_TEST" }),
            NullLogger<FlutterwavePaymentProvider>.Instance);

        await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "tx-diag",
            Amount = 10m,
            Currency = "NGN",
            Description = "diag",
            Metadata = new Dictionary<string, string> { ["email"] = "buyer@x.com" }
        });

        Assert.Equal(1, recorder.CounterTotalFor("bhengu_payments_charges_total", "flutterwave"));
        Assert.True(recorder.HistogramObservationsFor("bhengu_payments_charge_duration_ms", "flutterwave") >= 1);
    }
}
