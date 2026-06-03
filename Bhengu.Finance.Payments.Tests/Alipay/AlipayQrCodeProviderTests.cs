// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using Bhengu.Finance.Payments.Alipay.Configuration;
using Bhengu.Finance.Payments.Alipay.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.QrCode;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Alipay;

public class AlipayQrCodeProviderTests
{
    private static readonly RSA SharedRsa = RSA.Create(2048);
    private static readonly string MerchantPrivateKeyPem = Convert.ToBase64String(SharedRsa.ExportPkcs8PrivateKey());

    private static AlipayQrCodeProvider Create(StubHttpMessageHandler handler, AlipayOptions? opts = null)
    {
        opts ??= new AlipayOptions
        {
            ClientId = "ALIPAY_TEST",
            MerchantPrivateKey = MerchantPrivateKeyPem,
            NotifyUrl = "https://example.com/notify",
            UseSandbox = true
        };
        var http = new HttpClient(handler);
        return new AlipayQrCodeProvider(http, Options.Create(opts), NullLogger<AlipayQrCodeProvider>.Instance);
    }

    private static QrCodeRequest SampleDynamic(string reference = "ORDER-AP-1") => new()
    {
        Amount = 12.34m,
        Currency = "CNY",
        Description = "Alipay QR test",
        MerchantReference = reference,
        Format = QrFormat.Payload
    };

    [Fact]
    public void ProviderName_IsAlipay()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Equal("alipay", provider.ProviderName);
    }

    [Fact]
    public void Constructor_Throws_WhenClientIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ProviderConfigurationException>(() =>
            new AlipayQrCodeProvider(http,
                Options.Create(new AlipayOptions { MerchantPrivateKey = MerchantPrivateKeyPem }),
                NullLogger<AlipayQrCodeProvider>.Instance));
    }

    [Fact]
    public async Task GenerateQrAsync_ReturnsPayload_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/gateway.do", req.RequestUri!.AbsoluteUri);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"alipay_trade_precreate_response":{"code":"10000","msg":"Success","out_trade_no":"ORDER-AP-1","qr_code":"https://qr.alipay.com/bax01234567890"},"sign":"x"}
                """);
        });
        var provider = Create(handler);
        var qr = await provider.GenerateQrAsync(SampleDynamic());

        Assert.Equal("ORDER-AP-1", qr.Reference);
        Assert.Equal(QrFormat.Payload, qr.Format);
        Assert.Equal("https://qr.alipay.com/bax01234567890", qr.Payload);
        Assert.Equal(12.34m, qr.Amount);
        Assert.Equal("CNY", qr.Currency);
    }

    [Fact]
    public async Task GenerateQrAsync_ThrowsForPngFormat()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var req = SampleDynamic() with { Format = QrFormat.Png };
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.GenerateQrAsync(req));
        Assert.Contains("QrFormat.Png not supported", ex.Message);
    }

    [Fact]
    public async Task GenerateQrAsync_ThrowsForStaticQrWithoutAmount()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var req = SampleDynamic() with { Amount = null };
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.GenerateQrAsync(req));
        Assert.Contains("precreate requires a locked amount", ex.Message);
    }

    [Fact]
    public async Task GenerateQrAsync_ThrowsBhenguPaymentException_OnProviderReject()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"alipay_trade_precreate_response":{"code":"40002","msg":"Invalid params","sub_code":"isv.invalid-signature","sub_msg":"Bad sign"}}
                """));
        var provider = Create(handler);
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.GenerateQrAsync(SampleDynamic()));
        Assert.Equal("isv.invalid-signature", ex.ProviderErrorCode);
        Assert.Equal("Bad sign", ex.ProviderErrorMessage);
    }

    [Fact]
    public async Task GenerateQrAsync_Throws4xxAsPaymentDeclined()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "nope"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.GenerateQrAsync(SampleDynamic()));
    }

    [Fact]
    public async Task GenerateQrAsync_WrapsHttpRequestExceptionAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns fail"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.GenerateQrAsync(SampleDynamic()));
    }

    [Fact]
    public async Task GenerateQrAsync_ForwardsIdempotencyKey_WithoutFailing()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"alipay_trade_precreate_response":{"code":"10000","msg":"Success","out_trade_no":"ORDER-AP-1","qr_code":"https://qr/x"}}
                """));
        var provider = Create(handler);
        var req = SampleDynamic() with { IdempotencyKey = "idem-xyz" };
        var qr = await provider.GenerateQrAsync(req);
        Assert.Equal("https://qr/x", qr.Payload);
    }

    [Theory]
    [InlineData("TRADE_SUCCESS", PaymentStatus.Completed)]
    [InlineData("TRADE_FINISHED", PaymentStatus.Completed)]
    [InlineData("WAIT_BUYER_PAY", PaymentStatus.Pending)]
    [InlineData("TRADE_CLOSED", PaymentStatus.Cancelled)]
    [InlineData("UNKNOWN_STATE", PaymentStatus.Pending)]
    public async Task GetQrStatusAsync_MapsAllTradeStates(string raw, PaymentStatus expected)
    {
        var body = "{\"alipay_trade_query_response\":{\"code\":\"10000\",\"msg\":\"Success\",\"out_trade_no\":\"ORDER-AP-1\",\"trade_status\":\"" + raw + "\"}}";
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, body));
        var provider = Create(handler);
        var status = await provider.GetQrStatusAsync("ORDER-AP-1");
        Assert.Equal(expected, status);
    }

    [Fact]
    public async Task GetQrStatusAsync_TreatsTradeNotExistAsPending()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"alipay_trade_query_response":{"code":"40004","msg":"Business Failed","sub_code":"ACQ.TRADE_NOT_EXIST"}}
                """));
        var provider = Create(handler);
        var status = await provider.GetQrStatusAsync("ORDER-AP-1");
        Assert.Equal(PaymentStatus.Pending, status);
    }
}
