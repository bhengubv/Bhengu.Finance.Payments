// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.MPesa.Configuration;
using Bhengu.Finance.Payments.MPesa.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.MPesa;

public class MPesaPaymentProviderTests
{
    private static MPesaOptions DefaultOptions() => new()
    {
        ConsumerKey = "ck_test",
        ConsumerSecret = "cs_test",
        BusinessShortCode = "174379",
        Passkey = "bfb279f9aa9bdbcf158e97dd71a467cd2e0c893059b10f78e6b72ada1ed2c919",
        CallbackUrl = "https://example.com/mpesa/cb/tok123",
        CallbackUrlToken = "tok123",
        InitiatorName = "testapi",
        SecurityCredential = "Safaricom999!*!",
        QueueTimeoutUrl = "https://example.com/mpesa/timeout",
        ResultUrl = "https://example.com/mpesa/result",
        UseSandbox = true
    };

    private static MPesaPaymentProvider Create(StubHttpMessageHandler handler, MPesaOptions? opts = null)
    {
        opts ??= DefaultOptions();
        var http = new HttpClient(handler);
        return new MPesaPaymentProvider(http, Options.Create(opts), NullLogger<MPesaPaymentProvider>.Instance);
    }

    // Routes OAuth requests to a static token response, and lets the test handle the operation request.
    private static StubHttpMessageHandler OAuthAware(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> operationHandler) =>
        new((req, ct) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("oauth/v1/generate", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"access_token":"test-access-token","expires_in":"3599"}
                    """);
            return operationHandler(req, ct);
        });

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "254712345678",
        Amount = 100m,
        Currency = "KES",
        Description = "MPesa test"
    };

    [Fact]
    public void Constructor_Throws_WhenConsumerKeyMissing()
    {
        var opts = DefaultOptions();
        opts.ConsumerKey = string.Empty;
        Assert.Throws<ProviderConfigurationException>(() => Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), opts));
    }

    [Fact]
    public void Constructor_Throws_WhenPasskeyMissing()
    {
        var opts = DefaultOptions();
        opts.Passkey = string.Empty;
        Assert.Throws<ProviderConfigurationException>(() => Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), opts));
    }

    [Fact]
    public void ProviderName_IsMPesa()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Equal("mpesa", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsPendingResponse_OnAcceptedStkPush()
    {
        var handler = OAuthAware((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("stkpush", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"MerchantRequestID":"29115-34620561-1","CheckoutRequestID":"ws_CO_191220191020363925","ResponseCode":"0","ResponseDescription":"Success. Request accepted for processing","CustomerMessage":"Success. Request accepted for processing"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("ws_CO_191220191020363925", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal(100m, response.Amount);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = OAuthAware((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate limited"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = OAuthAware((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad request"));
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
    public async Task ProcessRefundAsync_ReturnsResponse_OnAcceptedReversal()
    {
        var handler = OAuthAware((req, _) =>
        {
            Assert.Contains("reversal", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"OriginatorConversationID":"71840-27539181-07","ConversationID":"AG_20191219_00004e48cf7e3533f581","ResponseCode":"0","ResponseDescription":"Accept the service request successfully."}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "LKXXXX1234",
            Amount = 50m,
            Reason = "Customer requested"
        });

        Assert.Equal("AG_20191219_00004e48cf7e3533f581", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, refund.Status);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnAcceptedB2C()
    {
        var handler = OAuthAware((req, _) =>
        {
            Assert.Contains("b2c", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"OriginatorConversationID":"5118-111210482-1","ConversationID":"AG_20191219_00005797af5d7d75f652","ResponseCode":"0","ResponseDescription":"Accept the service request successfully."}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "254712345678",
            Amount = 200m,
            Currency = "KES",
            Description = "Salary"
        });

        Assert.Equal("AG_20191219_00005797af5d7d75f652", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, payout.Status);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_WhenTokenMatches()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature("any payload", "tok123"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForWrongToken()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("any payload", "wrong-token"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenTokenNotConfigured()
    {
        var opts = DefaultOptions();
        opts.CallbackUrlToken = string.Empty;
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), opts);
        Assert.False(provider.VerifyWebhookSignature("any payload", "anything"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsCompletedEvent_ForResultCodeZero()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"Body":{"stkCallback":{"MerchantRequestID":"29115-34620561-1","CheckoutRequestID":"ws_CO_191220191020363925","ResultCode":0,"ResultDesc":"The service request is processed successfully."}}}
            """);
        Assert.NotNull(evt);
        Assert.Equal("ws_CO_191220191020363925", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("stkcallback.success", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsFailedEvent_ForNonZeroResultCode()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"Body":{"stkCallback":{"MerchantRequestID":"x","CheckoutRequestID":"chk_1","ResultCode":1032,"ResultDesc":"Request cancelled by user"}}}
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
