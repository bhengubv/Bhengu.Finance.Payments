// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Kashier.Configuration;
using Bhengu.Finance.Payments.Kashier.Internals;
using Bhengu.Finance.Payments.Kashier.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Kashier;

public class KashierPaymentProviderTests
{
    private const string ApiKey = "api_test_key";

    private static KashierPaymentProvider Create(StubHttpMessageHandler handler, KashierOptions? opts = null)
    {
        opts ??= new KashierOptions
        {
            ApiKey = ApiKey,
            MerchantId = "MID_1",
            SecretKey = "secret_kashier",
            Currency = "EGP",
            Mode = "test",
            UseSandbox = true
        };
        var http = new HttpClient(handler);
        return new KashierPaymentProvider(http, Options.Create(opts), NullLogger<KashierPaymentProvider>.Instance,
            new KashierIdempotencyCache(new InMemoryBhenguDistributedCache()));
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "tok_card_kashier",
        Amount = 200m,
        Currency = "EGP",
        Description = "Kashier test"
    };

    [Fact]
    public void Constructor_Throws_WhenApiKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new KashierPaymentProvider(http, Options.Create(new KashierOptions { MerchantId = "x" }), NullLogger<KashierPaymentProvider>.Instance,
                new KashierIdempotencyCache(new InMemoryBhenguDistributedCache())));
    }

    [Fact]
    public void Constructor_Throws_WhenMerchantIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new KashierPaymentProvider(http, Options.Create(new KashierOptions { ApiKey = "k" }), NullLogger<KashierPaymentProvider>.Instance,
                new KashierIdempotencyCache(new InMemoryBhenguDistributedCache())));
    }

    [Fact]
    public void ProviderName_IsKashier()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("kashier", provider.ProviderName);
    }

    [Fact]
    public void Capabilities_Include3DSAndTokenisationAndTypedWebhooks()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.ThreeDSecure));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Tokenisation));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.TypedWebhooks));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Idempotency));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.PartialRefund));
    }

    [Fact]
    public void Capabilities_DoNotIncludePayout()
    {
        // Kashier does not publicly document a payout/disbursement API, so the provider must not advertise it.
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.False(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Payout));
        Assert.IsNotAssignableFrom<Bhengu.Finance.Payments.Core.Interfaces.IPayoutProvider>(provider);
    }

    [Fact]
    public async Task ProcessPaymentAsync_PostsToCheckout_AndReturnsCompletedResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            // Charge goes to POST {REST}/checkout (NOT orders/{id}/payments).
            Assert.Equal("/checkout", req.RequestUri!.AbsolutePath);
            // Auth is the raw Secret Key in the Authorization header (no scheme prefix).
            Assert.True(req.Headers.TryGetValues("Authorization", out var auth));
            Assert.Equal("secret_kashier", auth!.First());
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"SUCCESS","response":{"transactionId":"TX_KASH_1","merchantOrderId":"ORD_1","kashierOrderId":"KO_1","status":"SUCCESS","amount":"200.00","currency":"EGP","card":{"result":"SUCCESS"}}}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());
        Assert.Equal("TX_KASH_1", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReportsFailed_WhenCardDeclined_UnderSuccessEnvelope()
    {
        // Kashier can return a SUCCESS envelope while the card itself was declined (card.result).
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"status":"SUCCESS","response":{"transactionId":"TX_D","status":"FAILED","card":{"result":"DECLINED"}}}
            """));
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());
        Assert.Equal(PaymentStatus.Failed, response.Status);
    }

    [Fact]
    public async Task ProcessPaymentAsync_TargetsSandboxBaseUrl_WhenUseSandbox()
    {
        Uri? seen = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            seen = req.RequestUri;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"SUCCESS","response":{"transactionId":"T","status":"SUCCESS"}}""");
        });
        var provider = Create(handler);
        await provider.ProcessPaymentAsync(SamplePayment());
        Assert.Equal("test-api.kashier.io", seen!.Host);
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
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.InternalServerError, "boom"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("network"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessRefundAsync_PutsToTransactionsRefundPath_AndReturnsRefunded()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            // Refund is PUT /orders/{orderId}/transactions/{transactionId}?operation=refund.
            Assert.Equal(HttpMethod.Put, req.Method);
            Assert.Equal("/orders/TX_KASH_1/transactions/TX_KASH_1", req.RequestUri!.AbsolutePath);
            Assert.Contains("operation=refund", req.RequestUri!.Query);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"SUCCESS","response":{"transactionId":"TX_KASH_1","status":"REFUNDED","amount":"100.00"}}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "TX_KASH_1",
            Amount = 100m,
            Reason = "Customer requested"
        });
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
        Assert.Equal(100m, refund.Amount);
    }

    [Fact]
    public async Task ProcessRefundAsync_SplitsCompositeReference_IntoOrderAndTransaction()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal("/orders/ORD_9/transactions/TX_9", req.RequestUri!.AbsolutePath);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"SUCCESS","response":{"transactionId":"TX_9","status":"REFUNDED"}}""");
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "ORD_9:TX_9",
            Amount = 10m,
            Reason = "partial"
        });
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
    }

    [Fact]
    public async Task GetOrderStatusAsync_GetsReconciliationEndpoint()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal("/payments/orders/ORD_1", req.RequestUri!.AbsolutePath);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"SUCCESS","response":{"transactionId":"TX_1","merchantOrderId":"ORD_1","status":"SUCCESS","amount":"200.00","currency":"EGP"}}
                """);
        });
        var provider = Create(handler);
        var result = await provider.GetOrderStatusAsync("ORD_1");
        Assert.Equal("TX_1", result.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, result.Status);
        Assert.Equal(200m, result.Amount);
    }

    [Fact]
    public async Task ProcessPaymentAsync_DedupesOnSameIdempotencyKey()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                "{\"status\":\"SUCCESS\",\"response\":{\"transactionId\":\"TX_" + calls + "\",\"status\":\"SUCCESS\"}}");
        });
        var provider = Create(handler);
        var r1 = await provider.ProcessPaymentAsync(SamplePayment() with { IdempotencyKey = "shared" });
        var r2 = await provider.ProcessPaymentAsync(SamplePayment() with { IdempotencyKey = "shared" });
        Assert.Equal(r1.GatewayReference, r2.GatewayReference);
        Assert.Equal(1, calls);
    }

    // === Webhook signature: signatureKeys (sorted) → RFC3986 query → HMAC-SHA256 hex, keyed by the API key ===

    private const string WebhookBody =
        "{\"event\":\"pay\",\"data\":{\"signatureKeys\":[\"amount\",\"currency\",\"merchantOrderId\",\"status\",\"transactionId\"]," +
        "\"amount\":\"100.00\",\"currency\":\"EGP\",\"merchantOrderId\":\"ORD_1\",\"status\":\"SUCCESS\",\"transactionId\":\"TX_1\"}}";

    private static string SignWebhook(string apiKey)
    {
        var canonical =
            $"amount={Uri.EscapeDataString("100.00")}&currency=EGP&merchantOrderId=ORD_1&status=SUCCESS&transactionId=TX_1";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignatureKeysHmac()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(WebhookBody, SignWebhook(ApiKey)));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature(WebhookBody, "0000"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenSignatureKeysMissing()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        // No signatureKeys → not a signable Kashier webhook.
        Assert.False(provider.VerifyWebhookSignature("""{"event":"pay","data":{"transactionId":"TX_1"}}""", "deadbeef"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenNoKeyConfigured()
    {
        var opts = new KashierOptions { ApiKey = "k", MerchantId = "m", SecretKey = "s", WebhookSecret = "" };
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), opts);
        // Signature computed with a different key must not verify.
        Assert.False(provider.VerifyWebhookSignature(WebhookBody, SignWebhook("totally-different-key")));
    }

    [Fact]
    public void BuildWebhookSignaturePayload_SortsKeysAlphabetically()
    {
        var canonical = KashierPaymentProvider.BuildWebhookSignaturePayload(WebhookBody);
        Assert.Equal(
            "amount=100.00&currency=EGP&merchantOrderId=ORD_1&status=SUCCESS&transactionId=TX_1",
            canonical);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsTypedChargeSucceeded_OnPaySuccess()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event":"pay","data":{"transactionId":"TX_PAY","status":"SUCCESS","amount":"100.00","currency":"EGP"}}
            """);
        Assert.NotNull(evt);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal("TX_PAY", typed.GatewayReference);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(100m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsTypedRefund_OnRefund()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event":"refund","data":{"transactionId":"TX_REF","status":"REFUNDED","amount":"50.00","currency":"EGP"}}
            """);
        Assert.NotNull(evt);
        var typed = Assert.IsType<RefundSucceededEvent>(evt);
        Assert.Equal(PaymentStatus.Refunded, typed.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsTypedChargeFailed_OnFailedStatus()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event":"pay","data":{"transactionId":"TX_FAIL","status":"FAILED","amount":"50.00","currency":"EGP","transactionResponseCode":"05"}}
            """);
        Assert.NotNull(evt);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal("05", typed.FailureCode);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("not json");
        Assert.Null(evt);
    }

    [Fact]
    public void BuildHostedPaymentUrl_TargetsCheckoutHostWithSignedHash()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var url = provider.BuildHostedPaymentUrl("ORD_42", 99.20m, "EGP");
        Assert.StartsWith("https://checkout.kashier.io?", url);
        Assert.Contains("merchantId=MID_1", url);
        Assert.Contains("orderId=ORD_42", url);
        Assert.Contains("hash=", url);
    }

    [Fact]
    public void ComputeOrderHash_MatchesKashierPublishedTestVector()
    {
        // Integration-guide vector: /?payment=mid-0-1.99.20.EGP keyed by "11111".
        var hash = KashierPaymentProvider.ComputeOrderHash("mid-0-1", "99", "20", "EGP", "11111");
        Assert.Equal("606a8a1307d64caf4e2e9bb724738f115a8972c27eccb2a8acd9194c357e4bec", hash);
    }
}
