// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.WeChatPay.Configuration;
using Bhengu.Finance.Payments.WeChatPay.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.WeChatPay;

public class WeChatPayPaymentProviderTests
{
    private static readonly RSA SharedRsa = RSA.Create(2048);
    private static readonly string MerchantPrivateKeyPem = Convert.ToBase64String(SharedRsa.ExportPkcs8PrivateKey());
    private static readonly string PlatformPublicKeyPem = Convert.ToBase64String(SharedRsa.ExportSubjectPublicKeyInfo());

    private static WeChatPayPaymentProvider Create(StubHttpMessageHandler handler, WeChatPayOptions? opts = null)
    {
        opts ??= new WeChatPayOptions
        {
            AppId = "wxAPPID",
            MerchantId = "1900000001",
            MerchantCertSerialNo = "ABCDEF1234567890",
            MerchantPrivateKey = MerchantPrivateKeyPem,
            V3ApiKey = "12345678901234567890123456789012",
            WeChatPayPlatformCertificate = PlatformPublicKeyPem,
            NotifyUrl = "https://example.com/wechat-notify",
            Currency = "CNY"
        };
        var http = new HttpClient(handler);
        return new WeChatPayPaymentProvider(http, Options.Create(opts), NullLogger<WeChatPayPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "OUT_TRADE_NO_001",
        Amount = 50m,
        Currency = "CNY",
        Description = "WeChat Pay test"
    };

    [Fact]
    public void Constructor_Throws_WhenAppIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ProviderConfigurationException>(() =>
            new WeChatPayPaymentProvider(http,
                Options.Create(new WeChatPayOptions { MerchantId = "X", MerchantCertSerialNo = "Y", MerchantPrivateKey = MerchantPrivateKeyPem }),
                NullLogger<WeChatPayPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenMerchantPrivateKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ProviderConfigurationException>(() =>
            new WeChatPayPaymentProvider(http,
                Options.Create(new WeChatPayOptions { AppId = "X", MerchantId = "Y", MerchantCertSerialNo = "Z" }),
                NullLogger<WeChatPayPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsWeChatPay()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Equal("wechatpay", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsPendingWithCodeUrl_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/v3/pay/transactions/native", req.RequestUri!.PathAndQuery);
            Assert.Equal("WECHATPAY2-SHA256-RSA2048", req.Headers.Authorization?.Scheme);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code_url":"weixin://wxpay/bizpayurl?pr=ABCXYZ"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("OUT_TRADE_NO_001", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal(50m, response.Amount);
        Assert.NotNull(response.RedirectUrl);
        Assert.Contains("weixin://", response.RedirectUrl);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate limited"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad params"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.ServiceUnavailable, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS fail"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsRefunded_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("/v3/refund/domestic/refunds", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"refund_id":"RF_WX_1","out_refund_no":"REFUND_X","status":"SUCCESS"}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "OUT_TRADE_NO_001",
            Amount = 25m,
            Reason = "Customer requested"
        });
        Assert.Equal("RF_WX_1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("/v3/transfer/batches", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"batch_id":"BATCH_WX_1","out_batch_no":"BATCH_X","batch_status":"ACCEPTED"}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "openid_user_1",
            Amount = 100m,
            Currency = "CNY",
            Description = "Test payout"
        });
        Assert.Equal("BATCH_WX_1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, payout.Status);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenPlatformCertMissing()
    {
        var provider = Create(
            new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)),
            new WeChatPayOptions
            {
                AppId = "X",
                MerchantId = "Y",
                MerchantCertSerialNo = "Z",
                MerchantPrivateKey = MerchantPrivateKeyPem,
                WeChatPayPlatformCertificate = ""
            });
        Assert.False(provider.VerifyWebhookSignature("payload", "sig=="));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string payload = """{"id":"EV-1","event_type":"TRANSACTION.SUCCESS"}""";
        var sig = SharedRsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sigB64 = Convert.ToBase64String(sig);

        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(payload, sigB64));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("anything", "tampered=="));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForTransactionSuccess()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"id":"EV-2","event_type":"TRANSACTION.SUCCESS","resource":{"original_type":"OUT_TRADE_NO_99"}}
            """);
        Assert.NotNull(evt);
        Assert.Equal("OUT_TRADE_NO_99", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("TRANSACTION.SUCCESS", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownEvent()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"id":"x","event_type":"PARTNER.UNKNOWN","resource":{"original_type":"x"}}
            """);
        Assert.Null(evt);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("not json");
        Assert.Null(evt);
    }

    [Fact]
    public void DecryptResource_RoundtripsAeadAes256Gcm()
    {
        const string key = "12345678901234567890123456789012"; // 32 chars (AES-256 key)
        const string nonce = "abcdef012345";                   // 12 bytes (AEAD-AES-GCM standard nonce size)
        const string aad = "transaction";
        const string plaintext = """{"trade_state":"SUCCESS","amount":{"total":100}}""";

        var keyBytes = Encoding.UTF8.GetBytes(key);
        var nonceBytes = Encoding.UTF8.GetBytes(nonce);
        var aadBytes = Encoding.UTF8.GetBytes(aad);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(keyBytes, 16))
            aes.Encrypt(nonceBytes, plainBytes, cipher, tag, aadBytes);

        var combined = new byte[cipher.Length + tag.Length];
        Buffer.BlockCopy(cipher, 0, combined, 0, cipher.Length);
        Buffer.BlockCopy(tag, 0, combined, cipher.Length, tag.Length);
        var ciphertextB64 = Convert.ToBase64String(combined);

        var provider = Create(
            new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)),
            new WeChatPayOptions
            {
                AppId = "wx", MerchantId = "1", MerchantCertSerialNo = "x",
                MerchantPrivateKey = MerchantPrivateKeyPem,
                V3ApiKey = key,
                WeChatPayPlatformCertificate = PlatformPublicKeyPem
            });

        var decrypted = provider.DecryptResource(ciphertextB64, nonce, aad);
        Assert.Equal(plaintext, decrypted);
    }
}
