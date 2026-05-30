// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Pesapal.Configuration;
using Bhengu.Finance.Payments.Pesapal.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Pesapal;

public class PesapalPaymentProviderTests
{
    private static PesapalPaymentProvider Create(StubHttpMessageHandler handler, PesapalOptions? opts = null)
    {
        opts ??= new PesapalOptions
        {
            ConsumerKey = "ck_test",
            ConsumerSecret = "cs_test",
            IpnId = "ipn-id-123",
            CallbackUrl = "https://merchant.example/return",
            Currency = "KES"
        };
        var http = new HttpClient(handler);
        return new PesapalPaymentProvider(http, Options.Create(opts), NullLogger<PesapalPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "merchant-ref-001",
        Amount = 1500m,
        Currency = "KES",
        Description = "Pesapal test order",
        Metadata = new Dictionary<string, string>
        {
            ["email"] = "buyer@example.com",
            ["phone_number"] = "+254700000000",
            ["country_code"] = "KE",
            ["first_name"] = "Aki",
            ["last_name"] = "Mwangi"
        }
    };

    private static HttpResponseMessage Auth() => StubHttpMessageHandler.Json(
        HttpStatusCode.OK,
        """{"token":"tok-abc","expiryDate":"2026-12-31T00:00:00Z"}""");

    [Fact]
    public void Constructor_Throws_WhenConsumerKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => Auth()));
        Assert.Throws<ProviderConfigurationException>(() =>
            new PesapalPaymentProvider(http, Options.Create(new PesapalOptions { ConsumerSecret = "cs" }),
                NullLogger<PesapalPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenConsumerSecretMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => Auth()));
        Assert.Throws<ProviderConfigurationException>(() =>
            new PesapalPaymentProvider(http, Options.Create(new PesapalOptions { ConsumerKey = "ck" }),
                NullLogger<PesapalPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsPesapal()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => Auth()));
        Assert.Equal("pesapal", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsPendingResponseWithRedirect_OnSuccess()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            calls++;
            if (req.RequestUri!.PathAndQuery.Contains("RequestToken")) return Auth();
            Assert.Contains("SubmitOrderRequest", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"order_tracking_id":"ot-001","merchant_reference":"merchant-ref-001","redirect_url":"https://pay.pesapal.com/iframe/ot-001","status":"200"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("ot-001", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal(1500m, response.Amount);
        Assert.Equal("https://pay.pesapal.com/iframe/ot-001", response.Message);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("RequestToken")) return Auth();
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
            if (req.RequestUri!.PathAndQuery.Contains("RequestToken")) return Auth();
            return StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "invalid amount");
        });
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("RequestToken")) return Auth();
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
    public async Task ProcessRefundAsync_ReturnsRefunded_WhenPesapalReports200Status()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("RequestToken")) return Auth();
            Assert.Contains("RefundRequest", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"200","message":"Refund accepted"}""");
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "ot-001",
            Amount = 500m,
            Reason = "Customer requested"
        });

        Assert.Equal("ot-001", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
        Assert.Equal(500m, refund.Amount);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenSecretConfigured_ButSignatureDoesNotMatch()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => Auth()));
        Assert.False(provider.VerifyWebhookSignature("payload", "wrong-secret"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_WhenSignatureEqualsConfiguredSecret()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => Auth()));
        Assert.True(provider.VerifyWebhookSignature("payload", "cs_test"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => Auth()));
        Assert.False(provider.VerifyWebhookSignature("payload", "tampered=="));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForIpnChangeNotification()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => Auth()));
        var evt = await provider.ParseWebhookAsync("""
            {"OrderTrackingId":"ot-001","OrderMerchantReference":"merchant-ref-001","OrderNotificationType":"IPNCHANGE"}
            """);
        Assert.NotNull(evt);
        Assert.Equal("ot-001", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, evt.Status);
        Assert.Equal("IPNCHANGE", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => Auth()));
        var evt = await provider.ParseWebhookAsync("not json");
        Assert.Null(evt);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_WhenOrderTrackingIdMissing()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => Auth()));
        var evt = await provider.ParseWebhookAsync("""{"OrderNotificationType":"IPNCHANGE"}""");
        Assert.Null(evt);
    }
}
