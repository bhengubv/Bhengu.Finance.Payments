// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Moniepoint.Configuration;
using Bhengu.Finance.Payments.Moniepoint.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Moniepoint;

/// <summary>
/// Tests the Moniepoint provider, which integrates Monnify (api.monnify.com): Basic→login→Bearer auth,
/// init-transaction checkout, initiate-refund, single disbursement, and the monnify-signature (HMAC-SHA512)
/// webhook. The stub answers the /api/v1/auth/login call first, then the operation.
/// </summary>
public class MoniepointPaymentProviderTests
{
    private const string LoginJson =
        """{"requestSuccessful":true,"responseMessage":"success","responseBody":{"accessToken":"tok-123","expiresIn":3600}}""";

    private static MoniepointPaymentProvider Create(Func<HttpRequestMessage, HttpResponseMessage> opResponse, MoniepointOptions? opts = null)
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            req.RequestUri!.AbsolutePath.Contains("auth/login", StringComparison.OrdinalIgnoreCase)
                ? StubHttpMessageHandler.Json(HttpStatusCode.OK, LoginJson)
                : opResponse(req));

        opts ??= new MoniepointOptions
        {
            ApiKey = "mpt-api-key", SecretKey = "mpt-secret", ContractCode = "CONTRACT-1",
            WalletAccountNumber = "0123456789", WebhookSecret = "webhook-test-secret",
            RedirectUrl = "https://example.com/redirect"
        };
        return new MoniepointPaymentProvider(new HttpClient(handler), Options.Create(opts), NullLogger<MoniepointPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "card", Amount = 100m, Currency = "NGN", Description = "Moniepoint test"
    };

    [Fact]
    public void Constructor_Throws_WhenApiKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() => new MoniepointPaymentProvider(
            http, Options.Create(new MoniepointOptions { SecretKey = "s", ContractCode = "c" }),
            NullLogger<MoniepointPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenSecretKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() => new MoniepointPaymentProvider(
            http, Options.Create(new MoniepointOptions { ApiKey = "k", ContractCode = "c" }),
            NullLogger<MoniepointPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenContractCodeMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() => new MoniepointPaymentProvider(
            http, Options.Create(new MoniepointOptions { ApiKey = "k", SecretKey = "s" }),
            NullLogger<MoniepointPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsMoniepoint() =>
        Assert.Equal("moniepoint", Create(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")).ProviderName);

    [Fact]
    public async Task ProcessPaymentAsync_InitsTransaction_ReturnsPendingWithCheckoutUrl()
    {
        var provider = Create(req =>
        {
            Assert.Contains("merchant/transactions/init-transaction", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"requestSuccessful":true,"responseMessage":"success","responseBody":{"transactionReference":"MNFY-TX-1","paymentReference":"mpt-1","checkoutUrl":"https://sandbox.monnify.com/checkout/MNFY-TX-1"}}
                """);
        });
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("MNFY-TX-1", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal("https://sandbox.monnify.com/checkout/MNFY-TX-1", response.RedirectUrl);
    }

    [Fact]
    public async Task ProcessRefundAsync_InitiatesRefund()
    {
        var provider = Create(req =>
        {
            Assert.Contains("refunds/initiate-refund", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"requestSuccessful":true,"responseMessage":"queued","responseBody":{"refundReference":"MNFY-RF-1","transactionReference":"MNFY-TX-1","refundAmount":50,"refundStatus":"COMPLETED"}}
                """);
        });
        var refund = await provider.ProcessRefundAsync(new RefundRequest { GatewayReference = "MNFY-TX-1", Amount = 50m, Reason = "Customer requested" });

        Assert.Equal("MNFY-RF-1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, refund.Status);
    }

    [Fact]
    public async Task ProcessPayoutAsync_SingleDisbursement()
    {
        var provider = Create(req =>
        {
            Assert.Contains("disbursements/single", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"requestSuccessful":true,"responseMessage":"ok","responseBody":{"reference":"MNFY-TFR-1","status":"SUCCESS","amount":500}}
                """);
        });
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest { DestinationToken = "058:1234567890", Amount = 500m, Currency = "NGN", Description = "Vendor payout" });

        Assert.Equal("MNFY-TFR-1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429_OnOperationRateLimit()
    {
        var provider = Create(_ => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate limited"));
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws4xxAsPaymentDeclined()
    {
        var provider = Create(_ => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad"));
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailable()
    {
        var provider = Create(_ => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_WrapsHttpRequestExceptionAsProviderUnavailable()
    {
        var provider = Create(_ => throw new HttpRequestException("DNS fail"));
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSha512Signature()
    {
        const string secret = "webhook-test-secret";
        const string payload = """{"eventType":"SUCCESSFUL_TRANSACTION","eventData":{"transactionReference":"MNFY-1"}}""";
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
        var sig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        Assert.True(Create(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")).VerifyWebhookSignature(payload, sig));
    }

    [Fact]
    public void VerifyWebhookSignature_FallsBackToSecretKey_WhenWebhookSecretEmpty()
    {
        const string secretKey = "mpt-secret";
        const string payload = """{"eventType":"SUCCESSFUL_TRANSACTION","eventData":{"transactionReference":"MNFY-1"}}""";
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secretKey));
        var sig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        var provider = Create(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"),
            new MoniepointOptions { ApiKey = "k", SecretKey = secretKey, ContractCode = "c", WebhookSecret = "" });
        Assert.True(provider.VerifyWebhookSignature(payload, sig));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTampered() =>
        Assert.False(Create(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")).VerifyWebhookSignature("anything", "deadbeef"));

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeSucceeded_ForSuccessfulTransaction()
    {
        var evt = await Create(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")).ParseWebhookAsync("""
            {"eventType":"SUCCESSFUL_TRANSACTION","eventData":{"transactionReference":"MNFY-99","amountPaid":100,"currencyCode":"NGN","paymentStatus":"PAID"}}
            """);
        Assert.NotNull(evt);
        Assert.Equal("MNFY-99", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownEvent() =>
        Assert.Null(await Create(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")).ParseWebhookAsync("""
            {"eventType":"SOME_UNKNOWN","eventData":{"transactionReference":"X"}}
            """));

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson() =>
        Assert.Null(await Create(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")).ParseWebhookAsync("not json"));
}
