// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Alipay.Configuration;
using Bhengu.Finance.Payments.Alipay.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Alipay;

public class AlipayPaymentProviderTests
{
    // Shared throwaway RSA keypair used for all tests in this fixture — generated once per process.
    private static readonly RSA SharedRsa = RSA.Create(2048);
    private static readonly string MerchantPrivateKeyPem = Convert.ToBase64String(SharedRsa.ExportPkcs8PrivateKey());
    private static readonly string AlipayPublicKeyPem = Convert.ToBase64String(SharedRsa.ExportSubjectPublicKeyInfo());

    private static AlipayPaymentProvider Create(StubHttpMessageHandler handler, AlipayOptions? opts = null)
    {
        opts ??= new AlipayOptions
        {
            ClientId = "ALIPAY_TEST",
            MerchantPrivateKey = MerchantPrivateKeyPem,
            AlipayPublicKey = AlipayPublicKeyPem,
            NotifyUrl = "https://example.com/webhook",
            RedirectUrl = "https://example.com/return",
            Currency = "USD",
            UseSandbox = true
        };
        var http = new HttpClient(handler);
        return new AlipayPaymentProvider(http, Options.Create(opts), NullLogger<AlipayPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "PR_ALIPAY_1",
        Amount = 100m,
        Currency = "USD",
        Description = "Alipay test"
    };

    [Fact]
    public void Constructor_Throws_WhenClientIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var ex = Assert.Throws<ProviderConfigurationException>(() =>
            new AlipayPaymentProvider(http,
                Options.Create(new AlipayOptions { MerchantPrivateKey = MerchantPrivateKeyPem }),
                NullLogger<AlipayPaymentProvider>.Instance));
        Assert.Contains("ClientId", ex.Message);
    }

    [Fact]
    public void Constructor_Throws_WhenMerchantPrivateKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ProviderConfigurationException>(() =>
            new AlipayPaymentProvider(http,
                Options.Create(new AlipayOptions { ClientId = "X" }),
                NullLogger<AlipayPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsAlipay()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Equal("alipay", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsCompleted_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/ams/api/v1/payments/pay", req.RequestUri!.PathAndQuery);
            Assert.True(req.Headers.Contains("client-id"));
            Assert.True(req.Headers.Contains("request-time"));
            Assert.True(req.Headers.Contains("signature"));
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"result":{"resultCode":"SUCCESS","resultStatus":"S","resultMessage":"ok"},"paymentId":"PAY-AP-1","paymentRequestId":"PR_ALIPAY_1","normalUrl":"https://qr.alipay.com/ABC"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("PAY-AP-1", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Equal(100m, response.Amount);
        Assert.Equal("USD", response.Currency);
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
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad request"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
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
            Assert.Contains("/ams/api/v1/payments/refund", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"result":{"resultCode":"SUCCESS","resultStatus":"S"},"refundId":"RF-AP-1","refundRequestId":"RF_X"}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "PAY-AP-1",
            Amount = 50m,
            Reason = "Customer requested"
        });
        Assert.Equal("RF-AP-1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
        Assert.Equal(50m, refund.Amount);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsCompleted_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("/ams/api/v1/payments/payout", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"result":{"resultCode":"SUCCESS","resultStatus":"S"},"payoutId":"PO-AP-1","payoutRequestId":"PO_X"}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "merchant@alipay.cn",
            Amount = 200m,
            Currency = "USD",
            Description = "Payout"
        });
        Assert.Equal("PO-AP-1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenPublicKeyMissing()
    {
        var provider = Create(
            new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)),
            new AlipayOptions
            {
                ClientId = "X",
                MerchantPrivateKey = MerchantPrivateKeyPem,
                AlipayPublicKey = ""
            });
        Assert.False(provider.VerifyWebhookSignature("payload", "sig=="));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string payload = """{"notifyType":"PAYMENT_RESULT","paymentId":"PAY-AP-1","result":{"resultCode":"SUCCESS"}}""";
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
    public async Task ParseWebhookAsync_ReturnsEvent_ForPaymentResult()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"notifyType":"PAYMENT_RESULT","paymentId":"PAY-AP-99","result":{"resultCode":"SUCCESS"}}
            """);
        Assert.NotNull(evt);
        Assert.Equal("PAY-AP-99", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("PAYMENT_RESULT", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownNotifyType()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"notifyType":"SOMETHING_ELSE","paymentId":"x","result":{"resultCode":"SUCCESS"}}
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
}
