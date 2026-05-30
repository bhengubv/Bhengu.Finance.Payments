// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Interswitch.Configuration;
using Bhengu.Finance.Payments.Interswitch.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Interswitch;

public class InterswitchPaymentProviderTests
{
    private const string TokenJson = """{"access_token":"isw-token-xyz","token_type":"bearer","expires_in":3600}""";

    private static InterswitchPaymentProvider Create(StubHttpMessageHandler handler, InterswitchOptions? opts = null)
    {
        opts ??= new InterswitchOptions
        {
            ClientId = "isw-client-id",
            ClientSecret = "isw-client-secret",
            MerchantCode = "MX12345",
            ProductId = "10101",
            WebhookSecret = "webhook-test-secret"
        };
        var http = new HttpClient(handler);
        return new InterswitchPaymentProvider(http, Options.Create(opts), NullLogger<InterswitchPaymentProvider>.Instance);
    }

    private static StubHttpMessageHandler TokenThen(Func<HttpRequestMessage, HttpResponseMessage> apiHandler) =>
        new((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth/token", StringComparison.OrdinalIgnoreCase))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, TokenJson);
            return apiHandler(req);
        });

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "TRF-CODE-1",
        Amount = 100m,
        Currency = "NGN",
        Description = "Interswitch test"
    };

    [Fact]
    public void Constructor_Throws_WhenClientIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new InterswitchPaymentProvider(http, Options.Create(new InterswitchOptions { ClientSecret = "x" }),
                NullLogger<InterswitchPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenClientSecretMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new InterswitchPaymentProvider(http, Options.Create(new InterswitchOptions { ClientId = "x" }),
                NullLogger<InterswitchPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsInterswitch()
    {
        var provider = Create(TokenThen(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("interswitch", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsCompletedResponse_OnSuccess()
    {
        var handler = TokenThen(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("quickteller/payments/advices", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"transactionRef":"ISW-TX-100","responseCode":"00","responseDescription":"Approved"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("ISW-TX-100", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Equal(100m, response.Amount);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = TokenThen(_ => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate limited"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = TokenThen(_ => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad payload"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = TokenThen(_ => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
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
    public async Task ProcessRefundAsync_ReturnsRefundedResponse_OnSuccess()
    {
        var handler = TokenThen(req =>
        {
            Assert.Contains("/refund", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"refundReference":"ISW-RF-1","responseCode":"00","responseDescription":"Refund queued"}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "ISW-TX-100",
            Amount = 50m,
            Reason = "Customer requested"
        });

        Assert.Equal("ISW-RF-1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, refund.Status);
        Assert.Equal(50m, refund.Amount);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = TokenThen(req =>
        {
            Assert.Contains("disbursements/transactions", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"transactionRef":"ISW-DISB-1","responseCode":"00","responseDescription":"Disbursement initiated"}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "058:1234567890",
            Amount = 1000m,
            Currency = "NGN",
            Description = "Vendor payout"
        });

        Assert.Equal("ISW-DISB-1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
        Assert.Equal(1000m, payout.Amount);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenSecretMissing()
    {
        var provider = Create(
            TokenThen(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            new InterswitchOptions
            {
                ClientId = "id",
                ClientSecret = "secret",
                WebhookSecret = ""
            });
        Assert.False(provider.VerifyWebhookSignature("payload", "sig"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string secret = "webhook-test-secret";
        const string payload = """{"eventType":"payment.successful","data":{"transactionRef":"ISW-1"}}""";
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
        var validSig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        var provider = Create(TokenThen(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(payload, validSig));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(TokenThen(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("anything", "deadbeef"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForPaymentSuccessful()
    {
        var provider = Create(TokenThen(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"eventType":"payment.successful","data":{"transactionRef":"ISW-99","status":"approved"}}
            """);
        Assert.NotNull(evt);
        Assert.Equal("ISW-99", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("payment.successful", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownEventType()
    {
        var provider = Create(TokenThen(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("""
            {"eventType":"some.unknown.event","data":{"transactionRef":"X"}}
            """));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(TokenThen(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
