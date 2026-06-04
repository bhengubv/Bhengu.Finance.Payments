// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.OPay.Configuration;
using Bhengu.Finance.Payments.OPay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.OPay;

public class OPayPaymentProviderTests
{
    private static OPayPaymentProvider Create(StubHttpMessageHandler handler, OPayOptions? opts = null)
    {
        opts ??= new OPayOptions
        {
            PublicKey = "opay-pub-key",
            SecretKey = "opay-secret-key",
            MerchantId = "MERCH123",
            Country = "NG",
            CallbackUrl = "https://example.com/callback",
            ReturnUrl = "https://example.com/return"
        };
        var http = new HttpClient(handler);
        return new OPayPaymentProvider(http, Options.Create(opts), NullLogger<OPayPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "CARD",
        Amount = 100m,
        Currency = "NGN",
        Description = "OPay test"
    };

    [Fact]
    public void Constructor_Throws_WhenPublicKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new OPayPaymentProvider(http,
                Options.Create(new OPayOptions { SecretKey = "s", MerchantId = "m" }),
                NullLogger<OPayPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenSecretKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new OPayPaymentProvider(http,
                Options.Create(new OPayOptions { PublicKey = "p", MerchantId = "m" }),
                NullLogger<OPayPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenMerchantIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new OPayPaymentProvider(http,
                Options.Create(new OPayOptions { PublicKey = "p", SecretKey = "s" }),
                NullLogger<OPayPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsOPay()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("opay", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsCompletedResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("cashier/create", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code":"00000","message":"OK","data":{"reference":"ref-1","orderNo":"OPAY-ORD-1","cashierUrl":"https://opay/p/1","status":"success"}}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("OPAY-ORD-1", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Equal(100m, response.Amount);
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
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad data"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.InternalServerError, "down"));
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
    public async Task ProcessRefundAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("refund/create", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code":"00000","message":"OK","data":{"refundId":"OPAY-RF-1","status":"success"}}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "OPAY-ORD-1",
            Amount = 50m,
            Reason = "Customer requested"
        });

        Assert.Equal("OPAY-RF-1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, refund.Status);
        Assert.Equal(50m, refund.Amount);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("payout/create", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code":"00000","message":"OK","data":{"orderNo":"OPAY-PO-1","status":"success"}}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "USR-9000",
            Amount = 500m,
            Currency = "NGN",
            Description = "Vendor payout"
        });

        Assert.Equal("OPAY-PO-1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string secret = "opay-secret-key";
        const string payload = """{"type":"transaction.success","payload":{"reference":"OPAY-1"}}""";
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
        var validSig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(payload, validSig));
    }

    [Fact]
    public void VerifyWebhookSignature_StripsBearerPrefix_AndReturnsTrue()
    {
        const string secret = "opay-secret-key";
        const string payload = """{"type":"transaction.success","payload":{"reference":"OPAY-1"}}""";
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
        var validSig = "Bearer " + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(payload, validSig));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("anything", "deadbeef"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForTransactionSuccess()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"type":"transaction.success","payload":{"reference":"OPAY-99","status":"success"}}
            """);
        Assert.NotNull(evt);
        Assert.Equal("OPAY-99", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("transaction.success", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsUnknownCategory_ForUnknownEventType()
    {
        // OPay surfaces unrecognised but verified events as Category=Unknown so consumers can log
        // and act on new upstream types even before the SDK is taught to classify them.
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"type":"some.weird.event","payload":{"reference":"X"}}
            """);
        Assert.NotNull(evt);
        Assert.Equal(Bhengu.Finance.Payments.Core.Models.Webhooks.WebhookEventCategory.Unknown, evt!.Category);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
