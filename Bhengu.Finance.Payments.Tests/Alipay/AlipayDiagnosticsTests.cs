// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Alipay.Configuration;
using Bhengu.Finance.Payments.Alipay.Providers;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Alipay;

/// <summary>OTel counter assertions for the Alipay provider family.</summary>
public class AlipayDiagnosticsTests
{
    [Fact]
    public async Task ProcessPaymentAsync_IncrementsChargesTotal()
    {
        using var recorder = new DiagnosticsCounterRecorder();
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"result":{"resultCode":"SUCCESS","resultStatus":"S"},"paymentRequestId":"alipay-diag","paymentId":"DIAG"}
            """));
        var provider = new AlipayPaymentProvider(
            new HttpClient(handler),
            Options.Create(new AlipayOptions { ClientId = "TEST", MerchantPrivateKey = "fakekey", AlipayPublicKey = "fakepub", UseSandbox = true }),
            NullLogger<AlipayPaymentProvider>.Instance);

        try
        {
            await provider.ProcessPaymentAsync(new PaymentRequest
            {
                PaymentMethodToken = "tok_diag",
                Amount = 10m,
                Currency = "CNY",
                Description = "diag"
            });
        }
        catch { /* counter still increments */ }

        Assert.True(recorder.CounterTotalFor("bhengu_payments_charges_total", "alipay") >= 1);
    }
}
