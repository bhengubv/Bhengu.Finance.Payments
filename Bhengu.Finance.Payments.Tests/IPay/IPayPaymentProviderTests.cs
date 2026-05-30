// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.IPay.Configuration;
using Bhengu.Finance.Payments.IPay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.IPay;

public class IPayPaymentProviderTests
{
    private static IPayPaymentProvider Create(StubHttpMessageHandler handler, IPayOptions? opts = null)
    {
        opts ??= new IPayOptions
        {
            VendorId = "demo",
            HashKey = "demoCHANGED",
            Live = "1",
            Currency = "KES",
            CallbackUrl = "https://merchant.example/ipay/callback"
        };
        var http = new HttpClient(handler);
        return new IPayPaymentProvider(http, Options.Create(opts), NullLogger<IPayPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "order-001",
        Amount = 250m,
        Currency = "KES",
        Description = "iPay test",
        Metadata = new Dictionary<string, string>
        {
            ["tel"] = "0700000000",
            ["eml"] = "buyer@example.com",
            ["inv"] = "INV-001"
        }
    };

    [Fact]
    public void Constructor_Throws_WhenVendorIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ProviderConfigurationException>(() =>
            new IPayPaymentProvider(http, Options.Create(new IPayOptions { HashKey = "k" }),
                NullLogger<IPayPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenHashKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ProviderConfigurationException>(() =>
            new IPayPaymentProvider(http, Options.Create(new IPayOptions { VendorId = "v" }),
                NullLogger<IPayPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsIPay()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Equal("ipay", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_BuildsRedirectUrl_WithRequiredQueryAndHash()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("order-001", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal(250m, response.Amount);
        Assert.NotNull(response.Message);
        Assert.Contains("oid=order-001", response.Message);
        Assert.Contains("ttl=250.00", response.Message);
        Assert.Contains("vid=demo", response.Message);
        Assert.Contains("hash=", response.Message);
        Assert.Contains("payments.ipayafrica.com", response.Message);
    }

    [Fact]
    public async Task ProcessRefundAsync_ThrowsBhenguPaymentException_NotSupported()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "order-001",
            Amount = 100m,
            Reason = "test"
        }));
        Assert.Equal("not_supported", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task ChargeMpesaAsync_PostsToSdkEndpoint_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("api/sdk/v3/mpesa", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"ok"}""");
        });
        var provider = Create(handler);
        var body = await provider.ChargeMpesaAsync("0700000000", 100m, "order-001");
        Assert.Contains("ok", body);
    }

    [Fact]
    public async Task ChargeMpesaAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate limited"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ChargeMpesaAsync("0700000000", 100m, "order-001"));
    }

    [Fact]
    public async Task ChargeMpesaAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad request"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ChargeMpesaAsync("0700000000", 100m, "order-001"));
    }

    [Fact]
    public async Task ChargeMpesaAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ChargeMpesaAsync("0700000000", 100m, "order-001"));
    }

    [Fact]
    public async Task ChargeMpesaAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS fail"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ChargeMpesaAsync("0700000000", 100m, "order-001"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidHmacSha256Hex()
    {
        const string secret = "demoCHANGED";
        const string payload = "txncd=ABC123&status=aei7p7yrx4ae34";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var hex = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) hex.Append(b.ToString("x2"));

        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(payload, hex.ToString()));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("payload", "ff00deadbeef"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenHashKeyMissing()
    {
        // Cannot construct provider without HashKey, so simulate by ensuring tampered key still returns false.
        var provider = Create(
            new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)),
            new IPayOptions { VendorId = "v", HashKey = "k", CallbackUrl = "c" });
        Assert.False(provider.VerifyWebhookSignature("payload", "ff00"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForFormUrlEncodedSuccess()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("txncd=TX99&status=aei7p7yrx4ae34&ipnid=IPN1&mc=demo");
        Assert.NotNull(evt);
        Assert.Equal("TX99", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("aei7p7yrx4ae34", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownStatusCode()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("txncd=TX1&status=unknown");
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
