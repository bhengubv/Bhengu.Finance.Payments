// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Slydepay.Configuration;
using Bhengu.Finance.Payments.Slydepay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Slydepay;

public class SlydepayPaymentProviderTests
{
    private static SlydepayPaymentProvider Create(StubHttpMessageHandler handler, SlydepayOptions? opts = null)
    {
        opts ??= new SlydepayOptions
        {
            EmailOrMobile = "merchant@example.com",
            MerchantKey = "mkey-123",
            Currency = "GHS",
            PaymentChannels = "7",
            CallbackUrl = "https://merchant.example/slyde/cb"
        };
        var http = new HttpClient(handler);
        return new SlydepayPaymentProvider(http, Options.Create(opts), NullLogger<SlydepayPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "order-001",
        Amount = 30m,
        Currency = "GHS",
        Description = "Slydepay test"
    };

    [Fact]
    public void Constructor_Throws_WhenEmailOrMobileMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ProviderConfigurationException>(() =>
            new SlydepayPaymentProvider(http, Options.Create(new SlydepayOptions { MerchantKey = "k" }),
                NullLogger<SlydepayPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenMerchantKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ProviderConfigurationException>(() =>
            new SlydepayPaymentProvider(http, Options.Create(new SlydepayOptions { EmailOrMobile = "x" }),
                NullLogger<SlydepayPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsSlydepay()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Equal("slydepay", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsCheckoutUrl_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("ProcessPaymentOrder", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"success":true,"errorMessage":null,"result":{"success":true,"payToken":"pt-001","qrCode":"qr","checkOutUrl":"https://app.slydepay.com.gh/pay/pt-001"}}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("pt-001", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal(30m, response.Amount);
        Assert.Contains("slydepay.com.gh/pay/pt-001", response.RedirectUrl);
    }

    [Fact]
    public async Task ProcessPaymentAsync_MapsFalseSuccessToFailed()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"success":false,"errorMessage":"validation failed","result":{"success":false,"payToken":null}}
            """));
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());
        Assert.Equal(PaymentStatus.Failed, response.Status);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad"));
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
    public async Task ProcessRefundAsync_ThrowsBhenguPaymentException_NotSupported()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "pt-001",
            Amount = 5m,
            Reason = "test"
        }));
        Assert.Equal("not_supported", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task VerifyTransactionAsync_PostsToVerifyEndpoint_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("VerifyTransactionStatus", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"success":true,"result":{"transactionStatus":"CONFIRMED"}}""");
        });
        var provider = Create(handler);
        var body = await provider.VerifyTransactionAsync("pt-001", "order-001");
        Assert.Contains("CONFIRMED", body);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_WhenSignatureMatchesMerchantKey()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature("payload", "mkey-123"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("payload", "wrong-key"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForConfirmedNotification()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"payToken":"pt-001","orderCode":"order-001","transactionStatus":"CONFIRMED"}
            """);
        Assert.NotNull(evt);
        Assert.Equal("pt-001", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("CONFIRMED", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownStatus()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"payToken":"pt-001","transactionStatus":"WAT"}
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
