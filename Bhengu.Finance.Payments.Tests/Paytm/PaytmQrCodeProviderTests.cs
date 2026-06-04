// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.QrCode;
using Bhengu.Finance.Payments.Paytm.Configuration;
using Bhengu.Finance.Payments.Paytm.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paytm;

public class PaytmQrCodeProviderTests
{
    private static PaytmQrCodeProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new PaytmOptions { MerchantId = "MID1", MerchantKey = "secret_key" }),
            NullLogger<PaytmQrCodeProvider>.Instance);

    [Fact]
    public async Task GenerateQrAsync_PostsToSmartQrCreate_AndReturnsQrCode()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("paymentservices/qr/create", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"body":{"resultInfo":{"resultStatus":"S"},"qrCodeId":"QR1","qrData":"upi://pay?pa=paytmqr@paytm&pn=Merchant&am=100&tr=ORDER1&cu=INR"}}
                """);
        });
        var provider = Create(handler);
        var qr = await provider.GenerateQrAsync(new QrCodeRequest
        {
            Amount = 100m,
            Currency = "INR",
            Description = "Test",
            MerchantReference = "ORDER1"
        });
        Assert.Equal("QR1", qr.Reference);
        Assert.Equal(QrFormat.Payload, qr.Format);
        Assert.Contains("upi://", qr.Payload);
        Assert.Equal(100m, qr.Amount);
    }

    [Fact]
    public async Task GenerateQrAsync_StaticQr_NullAmount()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"body":{"resultInfo":{"resultStatus":"S"},"qrCodeId":"QR_STATIC","qrData":"upi://pay?pa=paytmqr@paytm&pn=Merchant&tr=STATIC&cu=INR"}}
                """));
        var provider = Create(handler);
        var qr = await provider.GenerateQrAsync(new QrCodeRequest
        {
            Currency = "INR",
            Description = "Static",
            MerchantReference = "STATIC"
        });
        Assert.Null(qr.Amount);
        Assert.Equal("QR_STATIC", qr.Reference);
    }

    [Fact]
    public async Task GenerateQrAsync_FailureResult_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"body":{"resultInfo":{"resultStatus":"F","resultCode":"500","resultMsg":"Smart QR not configured"}}}
                """));
        var provider = Create(handler);
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.GenerateQrAsync(new QrCodeRequest
        {
            Currency = "INR",
            Description = "x",
            MerchantReference = "ORDER1"
        }));
    }

    [Fact]
    public async Task GetQrStatusAsync_MapsTxnSuccess_AsCompleted()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("paymentservices/qr/getStatus", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"body":{"resultInfo":{"resultStatus":"S"},"txnStatus":"TXN_SUCCESS"}}
                """);
        });
        var provider = Create(handler);
        var status = await provider.GetQrStatusAsync("QR1");
        Assert.Equal(PaymentStatus.Completed, status);
    }

    [Fact]
    public async Task GetQrStatusAsync_MapsTxnPending_AsPending()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"body":{"resultInfo":{"resultStatus":"S"},"txnStatus":"PENDING"}}
                """));
        var provider = Create(handler);
        var status = await provider.GetQrStatusAsync("QR1");
        Assert.Equal(PaymentStatus.Pending, status);
    }

    [Fact]
    public async Task GenerateQrAsync_OnNetworkFailure_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.GenerateQrAsync(new QrCodeRequest
        {
            Currency = "INR",
            Description = "x",
            MerchantReference = "ORDER1"
        }));
    }
}
