// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.WeChatPay.Configuration;
using Bhengu.Finance.Payments.WeChatPay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.WeChatPay;

/// <summary>OTel counter assertions for the WeChatPay provider family.</summary>
public class WeChatPayDiagnosticsTests
{
    [Fact]
    public async Task ProcessPaymentAsync_IncrementsChargesTotal()
    {
        using var recorder = new DiagnosticsCounterRecorder();
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"prepay_id":"wx-diag","code_url":"weixin://x"}
            """));
        var provider = new WeChatPayPaymentProvider(
            new HttpClient(handler),
            Options.Create(new WeChatPayOptions
            {
                AppId = "wx1234567890",
                MerchantId = "1900000000",
                MerchantPrivateKey = "fakekey",
                MerchantCertSerialNo = "ABCDEF",
                V3ApiKey = "0123456789ABCDEF0123456789ABCDEF"
            }),
            NullLogger<WeChatPayPaymentProvider>.Instance);

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

        Assert.True(recorder.CounterTotalFor("bhengu_payments_charges_total", "wechatpay") >= 1);
    }
}
