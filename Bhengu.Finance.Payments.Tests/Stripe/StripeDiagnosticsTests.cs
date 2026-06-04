// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Stripe;

/// <summary>
/// Verifies that the OpenTelemetry counters / histograms registered in
/// <c>BhenguPaymentDiagnostics</c> fire when Stripe public methods are invoked.
/// </summary>
public class StripeDiagnosticsTests
{
    [Fact]
    public async Task ProcessPaymentAsync_IncrementsChargesTotal()
    {
        using var recorder = new DiagnosticsCounterRecorder();
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"id":"pi_test","object":"payment_intent","amount":1000,"currency":"zar","status":"succeeded"}
            """));
        var provider = new StripePaymentProvider(
            new HttpClient(handler),
            Options.Create(new StripeOptions { SecretKey = "sk_test_fake", WebhookSecret = "whsec_test" }),
            NullLogger<StripePaymentProvider>.Instance);

        await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "pm_card_visa",
            Amount = 10m,
            Currency = "ZAR",
            Description = "diag"
        });

        Assert.Equal(1, recorder.CounterTotalFor("bhengu_payments_charges_total", "stripe"));
        Assert.True(recorder.HistogramObservationsFor("bhengu_payments_charge_duration_ms", "stripe") >= 1);
    }
}
