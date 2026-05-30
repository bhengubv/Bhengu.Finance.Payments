// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.OrangeMoney.Configuration;
using Bhengu.Finance.Payments.OrangeMoney.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.OrangeMoney;

public class OrangeMoneyPaymentProviderTests
{
    private static OrangeMoneyOptions DefaultOptions() => new()
    {
        ConsumerKey = "ck-orange",
        ConsumerSecret = "cs-orange",
        MerchantKey = "merchant-key-123",
        Country = "ci",
        ReturnUrl = "https://example.com/orange/return",
        CancelUrl = "https://example.com/orange/cancel",
        NotifUrl = "https://example.com/orange/notif",
        UseSandbox = true
    };

    private static OrangeMoneyPaymentProvider Create(StubHttpMessageHandler handler, OrangeMoneyOptions? opts = null)
    {
        opts ??= DefaultOptions();
        var http = new HttpClient(handler);
        return new OrangeMoneyPaymentProvider(http, Options.Create(opts), NullLogger<OrangeMoneyPaymentProvider>.Instance);
    }

    private static StubHttpMessageHandler OAuthAware(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> operationHandler) =>
        new((req, ct) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("oauth/v2/token", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"access_token":"orange-test-token","token_type":"Bearer","expires_in":3599}
                    """);
            return operationHandler(req, ct);
        });

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "tok",
        Amount = 1000m,
        Currency = "XOF",
        Description = "Orange test"
    };

    [Fact]
    public void Constructor_Throws_WhenConsumerKeyMissing()
    {
        var opts = DefaultOptions();
        opts.ConsumerKey = string.Empty;
        Assert.Throws<ProviderConfigurationException>(() => Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), opts));
    }

    [Fact]
    public void Constructor_Throws_WhenMerchantKeyMissing()
    {
        var opts = DefaultOptions();
        opts.MerchantKey = string.Empty;
        Assert.Throws<ProviderConfigurationException>(() => Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), opts));
    }

    [Fact]
    public void ProviderName_IsOrangeMoney()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Equal("orangemoney", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsResponseWithPaymentUrl_OnSuccess()
    {
        var handler = OAuthAware((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("orange-money-webpay/ci/v1/webpayment", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"INITIATED","message":"OK","pay_token":"PAY-TOK-XYZ","payment_url":"https://webpayment.orange-money.com/pay/abc","notif_token":"NTF-TOK-123"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("PAY-TOK-XYZ", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal("https://webpayment.orange-money.com/pay/abc", response.Message);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = OAuthAware((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "limit"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = OAuthAware((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = OAuthAware((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = OAuthAware((_, _) => throw new HttpRequestException("DNS fail"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessRefundAsync_Throws_BecauseRefundNotSupported()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "PAY-TOK-XYZ",
            Amount = 100m,
            Reason = "test"
        }));
        Assert.Contains("merchant portal", ex.Message);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_WhenNotifTokenMatches()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        const string payload = """{"status":"SUCCESS","pay_token":"PAY-TOK-XYZ","notif_token":"NTF-TOK-123","order_id":"ord"}""";
        Assert.True(provider.VerifyWebhookSignature(payload, "NTF-TOK-123"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForWrongNotifToken()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        const string payload = """{"status":"SUCCESS","pay_token":"PAY-TOK-XYZ","notif_token":"NTF-TOK-123"}""";
        Assert.False(provider.VerifyWebhookSignature(payload, "WRONG-TOKEN"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenPayloadMissingNotifToken()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("""{"status":"SUCCESS","pay_token":"PAY-TOK-XYZ"}""", "anything"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ValidPayload_ReturnsCompletedEvent()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"status":"SUCCESS","pay_token":"PAY-TOK-XYZ","order_id":"ord-1","notif_token":"NTF","amount":"1000","currency":"XOF","txnid":"TXN-1"}
            """);

        Assert.NotNull(evt);
        Assert.Equal("PAY-TOK-XYZ", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("success", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsFailedEvent_ForFailedStatus()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"status":"FAILED","pay_token":"PAY-TOK-XYZ","order_id":"ord-1"}
            """);
        Assert.NotNull(evt);
        Assert.Equal(PaymentStatus.Failed, evt!.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
