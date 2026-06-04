// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.PayShap.Configuration;
using Bhengu.Finance.Payments.PayShap.Models.Requests;
using Bhengu.Finance.Payments.PayShap.Models.Responses;
using Bhengu.Finance.Payments.PayShap.Providers;
using Bhengu.Finance.Payments.PayShap.Services.Interfaces;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayShap;

/// <summary>OTel counter assertions for the PayShap provider family.</summary>
public class PayShapDiagnosticsTests
{
    [Fact]
    public async Task ProcessPaymentAsync_IncrementsChargesTotal()
    {
        using var recorder = new DiagnosticsCounterRecorder();
        var mockService = new Mock<IPayShapService>();
        mockService.Setup(s => s.InitiateRtcPaymentAsync(It.IsAny<RtcPaymentRequest>()))
            .ReturnsAsync(new RtcPaymentResponse
            {
                Status = "COMPLETED",
                TransactionId = "PS-DIAG"
            });

        var provider = new PayShapPaymentProvider(
            mockService.Object,
            Options.Create(new PayShapSettings { SignatureKey = "k" }),
            NullLogger<PayShapPaymentProvider>.Instance);

        try
        {
            await provider.ProcessPaymentAsync(new PaymentRequest
            {
                PaymentMethodToken = "0821234567",
                Amount = 100m,
                Currency = "ZAR",
                Description = "diag",
                Metadata = new Dictionary<string, string>
                {
                    ["payshap.payer.account"] = "1234",
                    ["payshap.payer.bank_code"] = "FNB",
                    ["payshap.payer.name"] = "Payer",
                    ["payshap.payee.account"] = "5678",
                    ["payshap.payee.bank_code"] = "ABSA",
                    ["payshap.payee.name"] = "Payee"
                }
            });
        }
        catch { /* counter still increments via observability finally */ }

        Assert.True(recorder.CounterTotalFor("bhengu_payments_charges_total", "payshap") >= 1);
    }
}
