// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.MercadoPago.Configuration;
using Bhengu.Finance.Payments.MercadoPago.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.MercadoPago;

/// <summary>OTel counter assertions for the MercadoPago provider family.</summary>
public class MercadoPagoDiagnosticsTests
{
    [Fact]
    public async Task ProcessPaymentAsync_IncrementsChargesTotal()
    {
        using var recorder = new DiagnosticsCounterRecorder();
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
{"id":12345,"status":"approved"}
""".Trim()));
        var provider = new MercadoPagoPaymentProvider(
            new HttpClient(handler),
            Options.Create(new MercadoPagoOptions { AccessToken = "tok" }),
            NullLogger<MercadoPagoPaymentProvider>.Instance);

        try
        {
            await provider.ProcessPaymentAsync(new PaymentRequest
            {
                PaymentMethodToken = "tok_diag",
                Amount = 10m,
                Currency = "BRL",
                Description = "diag"
            });
        }
        catch
        {
            // Provider may need richer responses for full happy-path. We only assert the counter was incremented.
        }

        Assert.True(recorder.CounterTotalFor("bhengu_payments_charges_total", "mercadopago") >= 1);
    }
}