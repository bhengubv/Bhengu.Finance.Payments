// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Hubtel.Configuration;
using Bhengu.Finance.Payments.Hubtel.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Hubtel;

public class HubtelPaymentProviderTests
{
    private static HubtelPaymentProvider Create(StubHttpMessageHandler handler, HubtelOptions? opts = null)
    {
        opts ??= new HubtelOptions
        {
            ClientId = "ci",
            ClientSecret = "cs",
            MerchantAccountNumber = "1234567",
            WebhookSecret = "whsec",
            CallbackUrl = "https://merchant.example/hubtel/cb",
            ReturnUrl = "https://merchant.example/return",
            Currency = "GHS"
        };
        var http = new HttpClient(handler);
        return new HubtelPaymentProvider(http, Options.Create(opts), NullLogger<HubtelPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "client-ref-001",
        Amount = 12.50m,
        Currency = "GHS",
        Description = "Hubtel test order",
        Metadata = new Dictionary<string, string>
        {
            ["payeeName"] = "Kwame Mensah",
            ["payeeMobileNumber"] = "233244000000",
            ["payeeEmail"] = "kwame@example.com"
        }
    };

    [Fact]
    public void Constructor_Throws_WhenClientIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ProviderConfigurationException>(() =>
            new HubtelPaymentProvider(http, Options.Create(new HubtelOptions
            {
                ClientSecret = "s", MerchantAccountNumber = "1"
            }), NullLogger<HubtelPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenMerchantAccountNumberMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ProviderConfigurationException>(() =>
            new HubtelPaymentProvider(http, Options.Create(new HubtelOptions
            {
                ClientId = "c", ClientSecret = "s"
            }), NullLogger<HubtelPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsHubtel()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Equal("hubtel", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsCheckoutUrl_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("checkout/initiate", req.RequestUri!.PathAndQuery);
            Assert.NotNull(req.Headers.Authorization);
            Assert.Equal("Basic", req.Headers.Authorization!.Scheme);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"responseCode":"0000","status":"Success","data":{"checkoutUrl":"https://checkout.hubtel.com/abc","checkoutId":"ck-1","clientReference":"client-ref-001"}}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("ck-1", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal(12.50m, response.Amount);
        Assert.Equal("https://checkout.hubtel.com/abc", response.Message);
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
    public async Task ProcessRefundAsync_ReturnsRefundedResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("transactions/refund", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"Success","message":"ok","data":{"transactionId":"rf-1","status":"refunded"}}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "tx-1",
            Amount = 5m,
            Reason = "duplicate"
        });
        Assert.Equal("rf-1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
        Assert.Equal(5m, refund.Amount);
    }

    [Fact]
    public async Task ProcessPayoutAsync_PostsToSendMobileMoney_AndMapsCompleted()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("merchantaccount/merchants/", req.RequestUri!.PathAndQuery);
            Assert.Contains("send/mobilemoney", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"responseCode":"0000","status":"Success","data":{"transactionId":"po-1","clientReference":"po-ref","transactionStatus":"success"}}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "mtn-gh:233244000000",
            Amount = 50m,
            Currency = "GHS",
            Description = "Vendor"
        });
        Assert.Equal("po-1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws_OnMalformedDestinationToken()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "nochannel",
            Amount = 1m,
            Currency = "GHS",
            Description = "bad"
        }));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidHmacHex()
    {
        const string secret = "whsec";
        const string payload = """{"type":"payment.completed","data":{"clientReference":"x","status":"success"}}""";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(payload, hex));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("payload", "ff00deadbeef"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenWebhookSecretMissing()
    {
        var provider = Create(
            new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)),
            new HubtelOptions { ClientId = "c", ClientSecret = "s", MerchantAccountNumber = "1", WebhookSecret = "" });
        Assert.False(provider.VerifyWebhookSignature("payload", "ff00"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForPaymentSuccess()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"type":"payment.completed","data":{"clientReference":"client-ref-001","transactionId":"tx-99","status":"success","amount":12.50}}
            """);
        Assert.NotNull(evt);
        Assert.Equal("client-ref-001", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("not json");
        Assert.Null(evt);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownEventType()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"type":"some.unknown.event","data":{"clientReference":"x","status":"unknown"}}
            """);
        Assert.Null(evt);
    }
}
