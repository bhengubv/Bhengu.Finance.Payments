// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.QrCode;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.WeChatPay.Configuration;
using Bhengu.Finance.Payments.WeChatPay.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.WeChatPay;

public class WeChatPayQrCodeProviderTests
{
    private static readonly RSA SharedRsa = RSA.Create(2048);
    private static readonly string MerchantPrivateKeyPem = Convert.ToBase64String(SharedRsa.ExportPkcs8PrivateKey());

    private static WeChatPayQrCodeProvider Create(StubHttpMessageHandler handler, WeChatPayOptions? opts = null)
    {
        opts ??= new WeChatPayOptions
        {
            AppId = "wxAPPID",
            MerchantId = "1900000001",
            MerchantCertSerialNo = "ABCDEF1234567890",
            MerchantPrivateKey = MerchantPrivateKeyPem,
            V3ApiKey = "12345678901234567890123456789012",
            NotifyUrl = "https://example.com/wechat-notify",
            Currency = "CNY"
        };
        var http = new HttpClient(handler);
        return new WeChatPayQrCodeProvider(http, Options.Create(opts), NullLogger<WeChatPayQrCodeProvider>.Instance);
    }

    private static QrCodeRequest SampleDynamic(string reference = "OUT_TRADE_NO_QR_1") => new()
    {
        Amount = 5.50m,
        Currency = "CNY",
        Description = "WeChat QR test",
        MerchantReference = reference,
        Format = QrFormat.Payload
    };

    [Fact]
    public void ProviderName_IsWeChatPay()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Equal("wechatpay", provider.ProviderName);
    }

    [Fact]
    public void Constructor_Throws_WhenAppIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ProviderConfigurationException>(() =>
            new WeChatPayQrCodeProvider(http,
                Options.Create(new WeChatPayOptions
                {
                    MerchantId = "1", MerchantCertSerialNo = "x", MerchantPrivateKey = MerchantPrivateKeyPem
                }),
                NullLogger<WeChatPayQrCodeProvider>.Instance));
    }

    [Fact]
    public async Task GenerateQrAsync_ReturnsCodeUrl_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/v3/pay/transactions/native", req.RequestUri!.PathAndQuery);
            Assert.Equal("WECHATPAY2-SHA256-RSA2048", req.Headers.Authorization?.Scheme);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code_url":"weixin://wxpay/bizpayurl?pr=QRTEST01"}
                """);
        });
        var provider = Create(handler);
        var qr = await provider.GenerateQrAsync(SampleDynamic());

        Assert.Equal("OUT_TRADE_NO_QR_1", qr.Reference);
        Assert.Equal(QrFormat.Payload, qr.Format);
        Assert.Equal("weixin://wxpay/bizpayurl?pr=QRTEST01", qr.Payload);
        Assert.Equal(5.50m, qr.Amount);
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
        Assert.Contains("requires a locked amount", ex.Message);
    }

    [Fact]
    public async Task GenerateQrAsync_Throws4xxAsPaymentDeclined()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, """{"code":"INVALID_REQUEST","message":"bad amount"}"""));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.GenerateQrAsync(SampleDynamic()));
    }

    [Fact]
    public async Task GenerateQrAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate limited"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.GenerateQrAsync(SampleDynamic()));
    }

    [Fact]
    public async Task GenerateQrAsync_WrapsHttpRequestExceptionAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS fail"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.GenerateQrAsync(SampleDynamic()));
    }

    [Fact]
    public async Task GenerateQrAsync_ForwardsIdempotencyKey_WithoutFailing()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"code_url":"weixin://x"}"""));
        var provider = Create(handler);
        var req = SampleDynamic() with { IdempotencyKey = "idem-1" };
        var qr = await provider.GenerateQrAsync(req);
        Assert.Equal("weixin://x", qr.Payload);
    }

    [Theory]
    [InlineData("SUCCESS", PaymentStatus.Completed)]
    [InlineData("NOTPAY", PaymentStatus.Pending)]
    [InlineData("USERPAYING", PaymentStatus.Pending)]
    [InlineData("CLOSED", PaymentStatus.Cancelled)]
    [InlineData("REVOKED", PaymentStatus.Cancelled)]
    [InlineData("REFUND", PaymentStatus.Refunded)]
    [InlineData("PAYERROR", PaymentStatus.Failed)]
    [InlineData("UNKNOWN_STATE", PaymentStatus.Pending)]
    public async Task GetQrStatusAsync_MapsAllTradeStates(string raw, PaymentStatus expected)
    {
        var body = "{\"appid\":\"wx\",\"mchid\":\"1900000001\",\"out_trade_no\":\"OUT_TRADE_NO_QR_1\",\"trade_state\":\"" + raw + "\"}";
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains("/v3/pay/transactions/out-trade-no/", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, body);
        });
        var provider = Create(handler);
        var status = await provider.GetQrStatusAsync("OUT_TRADE_NO_QR_1");
        Assert.Equal(expected, status);
    }
}
