// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Ozow.Configuration;
using Bhengu.Finance.Payments.Ozow.Internals;
using Bhengu.Finance.Payments.Ozow.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Ozow;

public class OzowPaymentProviderTests
{
    private static OzowPaymentProvider Create(StubHttpMessageHandler handler, OzowOptions? opts = null)
    {
        opts ??= new OzowOptions { SiteCode = "TEST", PrivateKey = "priv", ApiKey = "apik", UseSandbox = true };
        var http = new HttpClient(handler);
        return new OzowPaymentProvider(http, Options.Create(opts), NullLogger<OzowPaymentProvider>.Instance,
            new OzowIdempotencyCache(new InMemoryBhenguDistributedCache()));
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "ozow-token",
        Amount = 250m,
        Currency = "ZAR",
        Description = "Ozow test"
    };

    [Fact]
    public void Constructor_Throws_WhenSiteCodeMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new OzowPaymentProvider(http, Options.Create(new OzowOptions { PrivateKey = "x", ApiKey = "y" }), NullLogger<OzowPaymentProvider>.Instance,
                new OzowIdempotencyCache(new InMemoryBhenguDistributedCache())));
    }

    [Fact]
    public void Constructor_Throws_WhenPrivateKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new OzowPaymentProvider(http, Options.Create(new OzowOptions { SiteCode = "x", ApiKey = "y" }), NullLogger<OzowPaymentProvider>.Instance,
                new OzowIdempotencyCache(new InMemoryBhenguDistributedCache())));
    }

    [Fact]
    public void Constructor_Throws_WhenApiKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new OzowPaymentProvider(http, Options.Create(new OzowOptions { SiteCode = "x", PrivateKey = "y" }), NullLogger<OzowPaymentProvider>.Instance,
                new OzowIdempotencyCache(new InMemoryBhenguDistributedCache())));
    }

    [Fact]
    public void Capabilities_IncludeIdempotencyAndTypedWebhooks()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.TypedWebhooks));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Idempotency));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.PartialRefund));
        Assert.False(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Payout));
        Assert.False(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.ThreeDSecure));
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("postpaymentrequest", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"transactionId":"ozow_tx_1","status":"pending","paymentUrl":"https://sandbox.ozow.com/pay/ozow_tx_1"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());
        Assert.Equal("ozow_tx_1", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal("https://sandbox.ozow.com/pay/ozow_tx_1", response.RedirectUrl);
        Assert.Equal("Payment initiated", response.Message);
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
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad request"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("network down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Dedupes_OnSameIdempotencyKey()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                $$"""{"transactionId":"ozow_{{calls}}","status":"pending"}""");
        });
        var provider = Create(handler);
        var r1 = await provider.ProcessPaymentAsync(SamplePayment() with { IdempotencyKey = "key-1" });
        var r2 = await provider.ProcessPaymentAsync(SamplePayment() with { IdempotencyKey = "key-1" });
        Assert.Equal(r1.GatewayReference, r2.GatewayReference);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("refund", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"refundId":"ozow_rf_1","status":"completed"}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "ozow_tx_1",
            Amount = 100m,
            Reason = "Customer requested"
        });
        Assert.Equal("ozow_rf_1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, refund.Status);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("anything", "tampered"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string priv = "priv";
        const string payload = """{"transactionId":"ozow_99","status":"complete"}""";
        var hash = SHA512.HashData(Encoding.UTF8.GetBytes(payload + priv));
        var validSig = Convert.ToHexString(hash).ToLowerInvariant();

        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(payload, validSig));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsTypedChargeSucceeded_WhenComplete()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"transactionId":"ozow_99","transactionReference":"ref_99","status":"complete","amount":250.00}
            """);
        Assert.NotNull(evt);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal("ref_99", typed.GatewayReference);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(250m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_FallsBackToTransactionId_WhenNoReference()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"transactionId":"ozow_99","status":"complete","amount":250.00}
            """);
        Assert.NotNull(evt);
        Assert.Equal("ozow_99", evt!.GatewayReference);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsTypedChargeFailed_WhenError()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"transactionId":"ozow_99","status":"error","amount":250.00,"statusMessage":"declined"}
            """);
        Assert.NotNull(evt);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal("declined", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
