// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Paytm.Configuration;
using Bhengu.Finance.Payments.Paytm.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paytm;

public class PaytmPaymentProviderTests
{
    private static PaytmPaymentProvider Create(StubHttpMessageHandler handler, PaytmOptions? opts = null)
    {
        opts ??= new PaytmOptions
        {
            MerchantId = "TESTMERCHANT01",
            MerchantKey = "test_merchant_key_super_secret",
            WebsiteName = "WEBSTAGING",
            CallbackUrl = "https://merchant.example.com/paytm/callback",
            UseSandbox = true
        };
        var http = new HttpClient(handler);
        return new PaytmPaymentProvider(http, Options.Create(opts), NullLogger<PaytmPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "CUST_001",
        Amount = 100m,
        Currency = "INR",
        Description = "Paytm test",
        Metadata = new Dictionary<string, string>
        {
            ["orderId"] = "ORDER_001",
            ["custId"] = "CUST_001",
            ["mobile"] = "9999999999",
            ["email"] = "buyer@example.com"
        }
    };

    [Fact]
    public void Constructor_Throws_WhenMerchantIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new PaytmPaymentProvider(http, Options.Create(new PaytmOptions { MerchantKey = "x" }),
                NullLogger<PaytmPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenMerchantKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new PaytmPaymentProvider(http, Options.Create(new PaytmOptions { MerchantId = "x" }),
                NullLogger<PaytmPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsPaytm()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("paytm", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_InitiatesTransaction_AndReturnsCheckoutUrl()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("theia/api/v1/initiateTransaction", req.RequestUri!.PathAndQuery);
            Assert.Contains("mid=TESTMERCHANT01", req.RequestUri.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"body":{"resultInfo":{"resultStatus":"S","resultCode":"0000","resultMsg":"Success"},"txnToken":"f0b714b8118541c4b8ab57b8f3c2a3a8"},"head":{"responseTimestamp":"123","signature":"sig"}}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("ORDER_001", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status); // "S" -> Completed in our map
        Assert.NotNull(response.RedirectUrl);
        Assert.Contains("showPaymentPage", response.RedirectUrl);
        Assert.Contains("txnToken=f0b714b8118541c4b8ab57b8f3c2a3a8", response.Message);
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
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "invalid request"));
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
            Assert.Contains("refund/apply", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"body":{"resultInfo":{"resultStatus":"TXN_SUCCESS","resultCode":"0000","resultMsg":"Refund Initiated"},"refundId":"203305000019200063","txnId":"20230510111212800110168847403211306","orderId":"ORDER_001"},"head":{"responseTimestamp":"1234567890"}}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "ORDER_001",
            Amount = 50m,
            Reason = "Customer requested"
        });

        Assert.Equal("203305000019200063", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, refund.Status);
        Assert.Equal(50m, refund.Amount);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("disburse/v1/order/wallet", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"body":{"resultInfo":{"resultStatus":"SUCCESS","resultCode":"0","resultMsg":"Disbursed"},"txnId":"PAYOUT_TXN_1","orderId":"PAYOUT_O_1"},"head":{}}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "9999999999",
            Amount = 500m,
            Currency = "INR",
            Description = "Vendor payout"
        });

        Assert.Equal("PAYOUT_TXN_1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
        Assert.Equal(500m, payout.Amount);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string secret = "test_merchant_key_super_secret";
        const string payload = """{"ORDERID":"ORDER_001","STATUS":"TXN_SUCCESS"}""";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var validSig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));

        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(payload, validSig));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("anything", "tampered=="));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForTxnSuccess()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"ORDERID":"ORDER_099","STATUS":"TXN_SUCCESS","TXNID":"20240510111212800110168847403211306"}
            """);
        Assert.NotNull(evt);
        Assert.Equal("ORDER_099", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("paytm.txn_success", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForLowercaseFieldFormat()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"orderId":"ORDER_088","status":"TXN_FAILURE"}
            """);
        Assert.NotNull(evt);
        Assert.Equal("ORDER_088", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Failed, evt.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_WhenOrderIdMissing()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("""{"STATUS":"TXN_SUCCESS"}"""));
    }
}
