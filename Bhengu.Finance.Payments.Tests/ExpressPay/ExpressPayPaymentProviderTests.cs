// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.ExpressPay.Configuration;
using Bhengu.Finance.Payments.ExpressPay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.ExpressPay;

public class ExpressPayPaymentProviderTests
{
    private static ExpressPayPaymentProvider Create(StubHttpMessageHandler handler, ExpressPayOptions? opts = null)
    {
        opts ??= new ExpressPayOptions
        {
            MerchantId = "demo-merchant",
            ApiKey = "demo-api-key",
            RedirectUrl = "https://merchant.example/return",
            PostUrl = "https://merchant.example/postback",
            Currency = "GHS"
        };
        var http = new HttpClient(handler);
        return new ExpressPayPaymentProvider(http, Options.Create(opts), NullLogger<ExpressPayPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "order-1",
        Amount = 75m,
        Currency = "GHS",
        Description = "ExpressPay test",
        Metadata = new Dictionary<string, string>
        {
            ["accountnumber"] = "0244000000",
            ["username"] = "buyer",
            ["email"] = "buyer@example.com",
            ["firstname"] = "Ama",
            ["lastname"] = "Owusu"
        }
    };

    [Fact]
    public void Constructor_Throws_WhenMerchantIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ProviderConfigurationException>(() =>
            new ExpressPayPaymentProvider(http, Options.Create(new ExpressPayOptions { ApiKey = "k" }),
                NullLogger<ExpressPayPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenApiKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ProviderConfigurationException>(() =>
            new ExpressPayPaymentProvider(http, Options.Create(new ExpressPayOptions { MerchantId = "m" }),
                NullLogger<ExpressPayPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsExpressPay()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Equal("expresspay", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsPaymentUrl_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("submit.php", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":1,"token":"tok-abc","payment_url":"https://sandbox.expresspaygh.com/api/checkout.php?token=tok-abc"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("tok-abc", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal(75m, response.Amount);
        Assert.Contains("checkout.php", response.RedirectUrl);
    }

    [Fact]
    public async Task ProcessPaymentAsync_MapsStatusZeroToFailed()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"status":0,"message":"bad request"}
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
            GatewayReference = "tok-abc",
            Amount = 10m,
            Reason = "test"
        }));
        Assert.Equal("not_supported", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task QueryStatusAsync_PostsToQueryPhp_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("query.php", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":1,"token":"tok-abc"}""");
        });
        var provider = Create(handler);
        var body = await provider.QueryStatusAsync("tok-abc");
        Assert.Contains("tok-abc", body);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_WhenSignatureMatchesConfiguredApiKey()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature("token=tok-abc&status=1", "demo-api-key"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("payload", "wrong"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForFormUrlEncodedSuccess()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("token=tok-abc&status=1&currency=GHS&amount=75.00");
        Assert.NotNull(evt);
        Assert.Equal("tok-abc", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownStatusCode()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("token=tok-abc&status=99");
        Assert.Null(evt);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("{not json}");
        Assert.Null(evt);
    }
}
