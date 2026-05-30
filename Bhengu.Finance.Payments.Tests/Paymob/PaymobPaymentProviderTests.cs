// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Paymob.Configuration;
using Bhengu.Finance.Payments.Paymob.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paymob;

public class PaymobPaymentProviderTests
{
    private static PaymobPaymentProvider Create(StubHttpMessageHandler handler, PaymobOptions? opts = null)
    {
        opts ??= new PaymobOptions
        {
            ApiKey = "api_test_key",
            HmacSecret = "hmac_secret_xxx",
            IntegrationId = 12345,
            IframeId = 99,
            Currency = "EGP"
        };
        var http = new HttpClient(handler);
        return new PaymobPaymentProvider(http, Options.Create(opts), NullLogger<PaymobPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "tok_card",
        Amount = 250m,
        Currency = "EGP",
        Description = "Paymob test",
        Metadata = new Dictionary<string, string>
        {
            ["email"] = "test@example.com",
            ["first_name"] = "Ada",
            ["last_name"] = "Lovelace",
            ["phone_number"] = "+201111111111"
        }
    };

    /// <summary>Simulate Paymob's 4-step accept handshake: auth → order → payment_key → (iframe).</summary>
    private static StubHttpMessageHandler FullSuccessHandler() =>
        new((req, _) =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.Contains("api/auth/tokens"))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"auth_token_abc"}""");
            if (path.Contains("api/ecommerce/orders"))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":777,"amount_cents":25000}""");
            if (path.Contains("api/acceptance/payment_keys"))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"pay_key_xyz"}""");
            if (path.Contains("api/acceptance/void_refund/refund"))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":888,"success":true,"is_refunded":true}""");
            if (path.Contains("api/disbursements/transactions"))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":999,"success":true}""");
            return StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "unexpected: " + path);
        });

    [Fact]
    public void Constructor_Throws_WhenApiKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new PaymobPaymentProvider(http, Options.Create(new PaymobOptions()), NullLogger<PaymobPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsPaymob()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("paymob", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsPendingWithIframeUrl_OnFullSuccess()
    {
        var provider = Create(FullSuccessHandler());
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("777", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal(250m, response.Amount);
        Assert.NotNull(response.Message);
        Assert.Contains("payment_token=pay_key_xyz", response.Message);
        Assert.Contains("iframes/99", response.Message);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws_WhenIntegrationIdMissing()
    {
        var opts = new PaymobOptions { ApiKey = "k", HmacSecret = "h" }; // no IntegrationId
        var provider = Create(FullSuccessHandler(), opts);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
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
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.Unauthorized, "bad key"));
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
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsRefundedResponse_OnSuccess()
    {
        var provider = Create(FullSuccessHandler());
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "555",
            Amount = 100m,
            Reason = "Customer requested"
        });
        Assert.Equal("888", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
        Assert.Equal(100m, refund.Amount);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsCompletedResponse_OnSuccess()
    {
        var provider = Create(FullSuccessHandler());
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "wallet-01099999999",
            Amount = 75m,
            Currency = "EGP",
            Description = "Vendor payout"
        });
        Assert.Equal("999", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidHmacSha512()
    {
        const string secret = "hmac_secret_xxx";
        const string payload = "id|123|success|true";
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
        var sig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(payload, sig));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("payload", "0000"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenHmacSecretMissing()
    {
        var opts = new PaymobOptions { ApiKey = "k", HmacSecret = "", IntegrationId = 1 };
        var provider = Create(
            new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)),
            opts);
        Assert.False(provider.VerifyWebhookSignature("payload", "sig"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsCompletedEvent_OnSuccess()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"type":"TRANSACTION","obj":{"id":555,"success":true,"is_refunded":false,"is_voided":false}}
            """);
        Assert.NotNull(evt);
        Assert.Equal("555", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsRefundedEvent_WhenIsRefundedTrue()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"type":"TRANSACTION","obj":{"id":777,"success":true,"is_refunded":true}}
            """);
        Assert.NotNull(evt);
        Assert.Equal(PaymentStatus.Refunded, evt!.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("not json");
        Assert.Null(evt);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_WhenObjMissing()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""{"type":"TRANSACTION"}""");
        Assert.Null(evt);
    }
}
