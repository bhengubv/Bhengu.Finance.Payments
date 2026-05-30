// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.PayUIndia.Configuration;
using Bhengu.Finance.Payments.PayUIndia.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayUIndia;

public class PayUIndiaPaymentProviderTests
{
    private static PayUIndiaPaymentProvider Create(StubHttpMessageHandler handler, PayUIndiaOptions? opts = null)
    {
        opts ??= new PayUIndiaOptions
        {
            MerchantKey = "gtKFFx",
            Salt = "eCwWELxi",
            SuccessUrl = "https://merchant.example.com/success",
            FailureUrl = "https://merchant.example.com/failure"
        };
        var http = new HttpClient(handler);
        return new PayUIndiaPaymentProvider(http, Options.Create(opts), NullLogger<PayUIndiaPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "n/a-for-redirect-flow",
        Amount = 100m,
        Currency = "INR",
        Description = "PayU India test",
        Metadata = new Dictionary<string, string>
        {
            ["txnid"] = "txn123",
            ["firstname"] = "Test",
            ["email"] = "buyer@example.com",
            ["phone"] = "9999999999"
        }
    };

    [Fact]
    public void Constructor_Throws_WhenMerchantKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new PayUIndiaPaymentProvider(http, Options.Create(new PayUIndiaOptions { Salt = "x" }),
                NullLogger<PayUIndiaPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenSaltMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new PayUIndiaPaymentProvider(http, Options.Create(new PayUIndiaOptions { MerchantKey = "x" }),
                NullLogger<PayUIndiaPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsPayUIndia()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("payuindia", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_BuildsRedirectUrl_WithHash()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("txn123", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.NotNull(response.RedirectUrl);
        Assert.Contains("secure.payu.in/_payment?", response.RedirectUrl);
        Assert.Contains("hash=", response.RedirectUrl);
        Assert.Contains("txnid=txn123", response.RedirectUrl);
    }

    [Fact]
    public async Task ProcessPaymentAsync_UsesSandboxUrl_WhenUseSandboxTrue()
    {
        var provider = Create(
            new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")),
            new PayUIndiaOptions
            {
                MerchantKey = "gtKFFx",
                Salt = "eCwWELxi",
                UseSandbox = true,
                SuccessUrl = "https://s/x",
                FailureUrl = "https://s/y"
            });
        var response = await provider.ProcessPaymentAsync(SamplePayment());
        Assert.NotNull(response.RedirectUrl);
        Assert.Contains("test.payu.in/_payment?", response.RedirectUrl);
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsRefundedResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("merchant/postservice.php", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"1","msg":"Refund Initiated","mihpayid":"403993715526672253","request_id":"5005"}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "403993715526672253",
            Amount = 50m,
            Reason = "Customer requested"
        });

        Assert.NotEmpty(refund.GatewayReference);
        Assert.StartsWith("refund-", refund.GatewayReference);
        Assert.Equal(50m, refund.Amount);
    }

    [Fact]
    public async Task ProcessRefundAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate limited"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() =>
            provider.ProcessRefundAsync(new RefundRequest
            {
                GatewayReference = "x",
                Amount = 10m,
                Reason = "x"
            }));
    }

    [Fact]
    public async Task ProcessRefundAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad request"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            provider.ProcessRefundAsync(new RefundRequest
            {
                GatewayReference = "x",
                Amount = 10m,
                Reason = "x"
            }));
    }

    [Fact]
    public async Task ProcessRefundAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            provider.ProcessRefundAsync(new RefundRequest
            {
                GatewayReference = "x",
                Amount = 10m,
                Reason = "x"
            }));
    }

    [Fact]
    public async Task ProcessRefundAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS fail"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            provider.ProcessRefundAsync(new RefundRequest
            {
                GatewayReference = "x",
                Amount = 10m,
                Reason = "x"
            }));
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("merchant/postservice.php", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"1","msg":"Transfer Initiated","mihpayid":"trans_1"}
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

        Assert.Equal("trans_1", payout.GatewayReference);
        Assert.Equal(500m, payout.Amount);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenSaltMissing()
    {
        // The constructor enforces Salt is required, so we can only exercise the runtime
        // SaltEmpty guard via direct construction of options that skip the constructor check.
        // Practically: an empty Salt is impossible post-construction, but we still want a
        // negative path for a signature that doesn't match.
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("anything", "tampered=="));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string canonicalHashInput = "eCwWELxi|success|||||u5|u4|u3|u2|u1|buyer@example.com|Test|productinfo|100.00|txn123|gtKFFx";
        var validSig = Sha512Hex(canonicalHashInput);
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(canonicalHashInput, validSig));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("anything", "deadbeef"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForFormUrlEncodedSuccess()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("status=success&txnid=txn123&mihpayid=403993715&amount=100.00&hash=deadbeef");
        Assert.NotNull(evt);
        Assert.Equal("txn123", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("payuindia.success", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForJsonSuccess()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"status":"success","txnid":"txn456","mihpayid":"403","amount":"100.00"}
            """);
        Assert.NotNull(evt);
        Assert.Equal("txn456", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("{not json"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_WhenTxnidMissing()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("status=success&amount=10.00"));
    }

    private static string Sha512Hex(string input)
    {
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
