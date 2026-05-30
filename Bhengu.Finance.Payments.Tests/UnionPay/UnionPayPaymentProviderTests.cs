// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.UnionPay.Configuration;
using Bhengu.Finance.Payments.UnionPay.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.UnionPay;

public class UnionPayPaymentProviderTests
{
    private static readonly RSA SharedRsa = RSA.Create(2048);
    private static readonly string SignCertPrivateKeyPem = Convert.ToBase64String(SharedRsa.ExportPkcs8PrivateKey());
    private static readonly string VerifyCertPublicKeyPem = Convert.ToBase64String(SharedRsa.ExportSubjectPublicKeyInfo());

    private static UnionPayPaymentProvider Create(StubHttpMessageHandler handler, UnionPayOptions? opts = null)
    {
        opts ??= new UnionPayOptions
        {
            MerId = "777290058110097",
            CertId = "68759585097",
            SignCertPrivateKey = SignCertPrivateKeyPem,
            VerifyCertPublicKey = VerifyCertPublicKeyPem,
            FrontUrl = "https://example.com/return",
            BackUrl = "https://example.com/notify",
            Currency = "156",
            Encoding = "UTF-8",
            UseSandbox = true
        };
        var http = new HttpClient(handler);
        return new UnionPayPaymentProvider(http, Options.Create(opts), NullLogger<UnionPayPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "ORD-UP-001",
        Amount = 100m,
        Currency = "156",
        Description = "UnionPay test"
    };

    [Fact]
    public void Constructor_Throws_WhenMerIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var ex = Assert.Throws<ProviderConfigurationException>(() =>
            new UnionPayPaymentProvider(http,
                Options.Create(new UnionPayOptions { CertId = "x", SignCertPrivateKey = SignCertPrivateKeyPem }),
                NullLogger<UnionPayPaymentProvider>.Instance));
        Assert.Contains("MerId", ex.Message);
    }

    [Fact]
    public void Constructor_Throws_WhenCertIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ProviderConfigurationException>(() =>
            new UnionPayPaymentProvider(http,
                Options.Create(new UnionPayOptions { MerId = "1", SignCertPrivateKey = SignCertPrivateKeyPem }),
                NullLogger<UnionPayPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenSignCertPrivateKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ProviderConfigurationException>(() =>
            new UnionPayPaymentProvider(http,
                Options.Create(new UnionPayOptions { MerId = "1", CertId = "2" }),
                NullLogger<UnionPayPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsUnionPay()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Equal("unionpay", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsRedirectFormUrl()
    {
        // ProcessPaymentAsync builds the redirect form locally; no HTTP call is made.
        var handler = new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("ProcessPayment must not POST."));
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("ORD-UP-001", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal(100m, response.Amount);
        Assert.NotNull(response.Message);
        Assert.Contains("frontTransReq.do", response.Message);
        Assert.Contains("orderId", response.Message);
        Assert.Contains("signature", response.Message);
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsRefunded_OnSuccessRespCode()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("/gateway/api/backTransReq.do", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Text(HttpStatusCode.OK, "respCode=00&respMsg=success&queryId=20260530000123456&orderId=RF20260530");
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "20260530000123456",
            Amount = 50m,
            Reason = "Customer requested"
        });
        Assert.Equal("20260530000123456", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
        Assert.Equal(50m, refund.Amount);
    }

    [Fact]
    public async Task ProcessRefundAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate limited"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "Q1", Amount = 1m, Reason = "x"
        }));
    }

    [Fact]
    public async Task ProcessRefundAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "Q1", Amount = 1m, Reason = "x"
        }));
    }

    [Fact]
    public async Task ProcessRefundAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "Q1", Amount = 1m, Reason = "x"
        }));
    }

    [Fact]
    public async Task ProcessRefundAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS fail"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "Q1", Amount = 1m, Reason = "x"
        }));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenVerifyCertMissing()
    {
        var provider = Create(
            new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)),
            new UnionPayOptions
            {
                MerId = "X", CertId = "Y",
                SignCertPrivateKey = SignCertPrivateKeyPem,
                VerifyCertPublicKey = ""
            });
        Assert.False(provider.VerifyWebhookSignature("respCode=00&signature=x", "x"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        // Recreate UnionPay 5.1 sign: sort keys, k=v&k=v, SHA256 hex, RSA-SHA256 sign hex.
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["queryId"] = "20260530000999",
            ["respCode"] = "00",
            ["txnAmt"] = "10000",
            ["txnType"] = "01"
        };
        var canonical = string.Join("&", fields.Select(kv => $"{kv.Key}={kv.Value}"));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var digestHex = Convert.ToHexString(digest).ToLowerInvariant();
        var sig = SharedRsa.SignData(Encoding.UTF8.GetBytes(digestHex), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sigB64 = Convert.ToBase64String(sig);

        var formBody = string.Join("&", fields.Select(kv => $"{kv.Key}={kv.Value}")) + $"&signature={Uri.EscapeDataString(sigB64)}";

        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(formBody, sigB64));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("respCode=00&txnType=01", "tampered=="));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForSuccessfulPayment()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("queryId=20260530000999&orderId=ORD1&respCode=00&txnType=01&signature=xx");
        Assert.NotNull(evt);
        Assert.Equal("20260530000999", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("01", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsRefunded_ForRefundTxnType()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("queryId=RF20260530&orderId=RF01&respCode=00&txnType=04");
        Assert.NotNull(evt);
        Assert.Equal(PaymentStatus.Refunded, evt!.Status);
        Assert.Equal("04", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_WhenRespCodeMissing()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("queryId=Q1&orderId=O1&txnType=01");
        Assert.Null(evt);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidBody()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("&&&&");
        Assert.Null(evt);
    }
}
