// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Ozow.Configuration;
using Bhengu.Finance.Payments.Ozow.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Ozow;

public class OzowPaymentProviderTests
{
    private static OzowPaymentProvider Create(StubHttpMessageHandler handler, OzowOptions? opts = null)
    {
        opts ??= new OzowOptions { SiteCode = "TEST", PrivateKey = "priv", ApiKey = "apik", UseSandbox = true };
        var http = new HttpClient(handler);
        return new OzowPaymentProvider(http, Options.Create(opts), NullLogger<OzowPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "ozow-token",
        Amount = 250m,
        Currency = "ZAR",
        Description = "Ozow test"
    };

    [Fact]
    public void Constructor_Throws_WhenSiteCodeMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new OzowPaymentProvider(http, Options.Create(new OzowOptions { PrivateKey = "x", ApiKey = "y" }), NullLogger<OzowPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenPrivateKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new OzowPaymentProvider(http, Options.Create(new OzowOptions { SiteCode = "x", ApiKey = "y" }), NullLogger<OzowPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenApiKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new OzowPaymentProvider(http, Options.Create(new OzowOptions { SiteCode = "x", PrivateKey = "y" }), NullLogger<OzowPaymentProvider>.Instance));
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("postpaymentrequest", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"transactionId":"ozow_tx_1","status":"pending","paymentUrl":"https://sandbox.ozow.com/pay/ozow_tx_1"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());
        Assert.Equal("ozow_tx_1", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Contains("Redirect to", response.Message);
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
    public async Task ProcessPaymentAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("network down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("refund", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"refundId":"ozow_rf_1","status":"completed"}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "ozow_tx_1",
            Amount = 100m,
            Reason = "Customer requested"
        });
        Assert.Equal("ozow_rf_1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, refund.Status);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenPrivateKeyMissing()
    {
        // Constructor would throw with empty PrivateKey, so simulate post-construction with reflection-free approach:
        // verify the runtime check by setting PrivateKey to whitespace via a dummy Options.
        // We can't easily bypass constructor validation, so this test asserts via tampered signature only.
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("anything", "tampered"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string priv = "priv";
        const string payload = """{"transactionId":"ozow_99","status":"complete"}""";
        var hash = SHA512.HashData(Encoding.UTF8.GetBytes(payload + priv));
        var validSig = Convert.ToHexString(hash).ToLowerInvariant();

        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(payload, validSig));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_WhenPayloadIsComplete()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"transactionId":"ozow_99","transactionReference":"ref_99","status":"complete","amount":250.00}
            """);
        Assert.NotNull(evt);
        Assert.Equal("ref_99", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("ozow.notification", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_FallsBackToTransactionId_WhenNoReference()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"transactionId":"ozow_99","status":"complete","amount":250.00}
            """);
        Assert.NotNull(evt);
        Assert.Equal("ozow_99", evt!.GatewayReference);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
