// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.QrCode;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.UnionPay.Configuration;
using Bhengu.Finance.Payments.UnionPay.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.UnionPay;

public class UnionPayQrCodeProviderTests
{
    private static readonly RSA SharedRsa = RSA.Create(2048);
    private static readonly string PrivateKeyPem = Convert.ToBase64String(SharedRsa.ExportPkcs8PrivateKey());

    private static UnionPayQrCodeProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new UnionPayOptions
            {
                MerId = "777290058110097",
                CertId = "68759585097",
                SignCertPrivateKey = PrivateKeyPem,
                BackUrl = "https://example.com/notify",
                Currency = "156",
                Encoding = "UTF-8",
                UseSandbox = true
            }),
            NullLogger<UnionPayQrCodeProvider>.Instance);

    [Fact]
    public async Task GenerateQrAsync_PostsBackTransReq_AndReturnsQrCode()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("backTransReq.do", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Text(HttpStatusCode.OK, "respCode=00&qrCode=https%3A%2F%2Fqr.95516.com%2F00010000%2F012345");
        });
        var provider = Create(handler);

        var qr = await provider.GenerateQrAsync(new QrCodeRequest
        {
            Amount = 100m,
            Currency = "CNY",
            Description = "Test",
            MerchantReference = "ORDQR001"
        });

        Assert.Equal("ORDQR001", qr.Reference);
        Assert.Equal(QrFormat.Payload, qr.Format);
        Assert.Contains("qr.95516.com", qr.Payload);
        Assert.Equal(100m, qr.Amount);
    }

    [Fact]
    public async Task GenerateQrAsync_NonSuccessRespCode_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.OK, "respCode=99&respMsg=Failed"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.GenerateQrAsync(new QrCodeRequest
        {
            Currency = "CNY",
            Description = "x",
            MerchantReference = "ORDQR002"
        }));
    }

    [Fact]
    public async Task GetQrStatusAsync_RespCode00_ReturnsCompleted()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("queryTrans.do", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Text(HttpStatusCode.OK, "respCode=00&origRespCode=00");
        });
        var provider = Create(handler);
        var status = await provider.GetQrStatusAsync("ORDQR001");
        Assert.Equal(PaymentStatus.Completed, status);
    }

    [Fact]
    public async Task GetQrStatusAsync_OrigRespCode03_ReturnsPending()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.OK, "respCode=00&origRespCode=03"));
        var provider = Create(handler);
        var status = await provider.GetQrStatusAsync("ORDQR001");
        Assert.Equal(PaymentStatus.Pending, status);
    }

    [Fact]
    public async Task GenerateQrAsync_OnNetworkFailure_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.GenerateQrAsync(new QrCodeRequest
        {
            Currency = "CNY",
            Description = "x",
            MerchantReference = "ORDQR003"
        }));
    }
}
