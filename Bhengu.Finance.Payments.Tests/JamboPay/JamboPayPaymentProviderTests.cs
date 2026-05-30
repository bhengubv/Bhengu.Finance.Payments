// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.JamboPay.Configuration;
using Bhengu.Finance.Payments.JamboPay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.JamboPay;

public class JamboPayPaymentProviderTests
{
    private static JamboPayPaymentProvider Create(StubHttpMessageHandler handler, JamboPayOptions? opts = null)
    {
        opts ??= new JamboPayOptions
        {
            ApiKey = "key",
            ClientId = "cid",
            ClientSecret = "csec",
            MerchantCode = "MCH-001",
            WebhookSecret = "whsec",
            CallbackUrl = "https://merchant.example/return",
            Currency = "KES"
        };
        var http = new HttpClient(handler);
        return new JamboPayPaymentProvider(http, Options.Create(opts), NullLogger<JamboPayPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "txn-001",
        Amount = 500m,
        Currency = "KES",
        Description = "JamboPay test",
        Metadata = new Dictionary<string, string>
        {
            ["email"] = "buyer@example.com",
            ["msisdn"] = "254700000000",
            ["name"] = "Aki Mwangi",
            ["payment_method"] = "MPESA"
        }
    };

    private static HttpResponseMessage AuthOk() => StubHttpMessageHandler.Json(
        HttpStatusCode.OK,
        """{"access_token":"tok-xyz","token_type":"Bearer","expires_in":3600}""");

    [Fact]
    public void Constructor_Throws_WhenApiKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => AuthOk()));
        Assert.Throws<ProviderConfigurationException>(() =>
            new JamboPayPaymentProvider(http, Options.Create(new JamboPayOptions
            {
                ClientId = "c", ClientSecret = "s", MerchantCode = "m"
            }), NullLogger<JamboPayPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenMerchantCodeMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => AuthOk()));
        Assert.Throws<ProviderConfigurationException>(() =>
            new JamboPayPaymentProvider(http, Options.Create(new JamboPayOptions
            {
                ApiKey = "k", ClientId = "c", ClientSecret = "s"
            }), NullLogger<JamboPayPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsJamboPay()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => AuthOk()));
        Assert.Equal("jambopay", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsCheckoutUrl_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("oauth/token")) return AuthOk();
            Assert.Contains("payments/initiate", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"transaction_ref":"txn-001","status":"initiated","checkout_url":"https://pay.jambopay.com/c/abc"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("txn-001", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal(500m, response.Amount);
        Assert.Equal("https://pay.jambopay.com/c/abc", response.Message);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("oauth/token")) return AuthOk();
            return StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate limited");
        });
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("oauth/token")) return AuthOk();
            return StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad");
        });
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("oauth/token")) return AuthOk();
            return StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down");
        });
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
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("oauth/token")) return AuthOk();
            Assert.Contains("payments/refund", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"refund_id":"rf-001","status":"refunded","message":"ok"}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "txn-001",
            Amount = 100m,
            Reason = "test"
        });
        Assert.Equal("rf-001", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
        Assert.Equal(100m, refund.Amount);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsPayoutResponse_OnSuccess_ForMsisdnDestination()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("oauth/token")) return AuthOk();
            Assert.Contains("payouts/initiate", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"payout_id":"po-001","status":"completed"}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "msisdn:254700000000",
            Amount = 1000m,
            Currency = "KES",
            Description = "Vendor payout"
        });
        Assert.Equal("po-001", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
        Assert.Equal(1000m, payout.Amount);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidHmacHex()
    {
        const string secret = "whsec";
        const string payload = """{"event":"payment.completed","transaction_ref":"txn-001"}""";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        var provider = Create(new StubHttpMessageHandler((_, _) => AuthOk()));
        Assert.True(provider.VerifyWebhookSignature(payload, hex));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => AuthOk()));
        Assert.False(provider.VerifyWebhookSignature("payload", "ff00deadbeef"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenWebhookSecretMissing()
    {
        var provider = Create(
            new StubHttpMessageHandler((_, _) => AuthOk()),
            new JamboPayOptions
            {
                ApiKey = "k", ClientId = "c", ClientSecret = "s", MerchantCode = "m", WebhookSecret = ""
            });
        Assert.False(provider.VerifyWebhookSignature("payload", "ff00"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForPaymentCompleted()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => AuthOk()));
        var evt = await provider.ParseWebhookAsync("""
            {"event":"payment.completed","transaction_ref":"txn-001","status":"successful"}
            """);
        Assert.NotNull(evt);
        Assert.Equal("txn-001", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("payment.completed", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownEventType()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => AuthOk()));
        var evt = await provider.ParseWebhookAsync("""
            {"event":"some.unknown.event","transaction_ref":"txn-x"}
            """);
        Assert.Null(evt);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => AuthOk()));
        var evt = await provider.ParseWebhookAsync("not json");
        Assert.Null(evt);
    }
}
