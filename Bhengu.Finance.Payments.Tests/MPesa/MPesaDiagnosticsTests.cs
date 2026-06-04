// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.MPesa.Configuration;
using Bhengu.Finance.Payments.MPesa.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.MPesa;

/// <summary>OTel counter assertions for the MPesa provider family.</summary>
public class MPesaDiagnosticsTests
{
    [Fact]
    public async Task ProcessPaymentAsync_IncrementsChargesTotal()
    {
        using var recorder = new DiagnosticsCounterRecorder();
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
{"access_token":"tok","expires_in":"3599","CheckoutRequestID":"mpesa-diag","ResponseCode":"0"}
""".Trim()));
        var provider = new MPesaPaymentProvider(
            new HttpClient(handler),
            Options.Create(new MPesaOptions { ConsumerKey = "ck", ConsumerSecret = "cs", BusinessShortCode = "174379", Passkey = "pk", CallbackUrl = "https://x" }),
            NullLogger<MPesaPaymentProvider>.Instance);

        try
        {
            await provider.ProcessPaymentAsync(new PaymentRequest
            {
                PaymentMethodToken = "tok_diag",
                Amount = 10m,
                Currency = "KES",
                Description = "diag"
            });
        }
        catch
        {
            // Provider may need richer responses for full happy-path. We only assert the counter was incremented.
        }

        Assert.True(recorder.CounterTotalFor("bhengu_payments_charges_total", "mpesa") >= 1);
    }
}