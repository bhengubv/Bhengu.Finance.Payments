// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Mukuru.Configuration;
using Bhengu.Finance.Payments.Mukuru.Internals;
using Bhengu.Finance.Payments.Mukuru.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Mukuru;

public class MukuruPaymentProviderTests
{
    private static MukuruPaymentProvider Create(StubHttpMessageHandler handler, MukuruOptions? opts = null)
    {
        opts ??= new MukuruOptions
        {
            ClientId = "client-test",
            ClientSecret = "secret-test",
            MerchantId = "MERCHANT-001",
            WebhookSecret = "webhook-mukuru-secret",
            SenderCountry = "ZA",
            DefaultCurrency = "ZAR",
            CallbackUrl = "https://example.com/mukuru-callback"
        };
        var http = new HttpClient(handler);
        return new MukuruPaymentProvider(http, Options.Create(opts), NullLogger<MukuruPaymentProvider>.Instance,
            new MukuruIdempotencyCache(new InMemoryBhenguDistributedCache()));
    }

    private static StubHttpMessageHandler HandlerWithTokenAnd(
        Func<HttpRequestMessage, HttpResponseMessage> apiHandler) =>
        new((req, _) => req.RequestUri!.PathAndQuery.Contains("auth/token", StringComparison.OrdinalIgnoreCase)
            ? StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"access_token":"mukuru-tok-123","token_type":"Bearer","expires_in":3600}
                """)
            : apiHandler(req));

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "topup-ref-1",
        Amount = 2500m,
        Currency = "ZAR",
        Description = "Wallet topup"
    };

    [Fact]
    public void Constructor_Throws_WhenClientIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new MukuruPaymentProvider(http, Options.Create(new MukuruOptions { ClientSecret = "x" }),
                NullLogger<MukuruPaymentProvider>.Instance,
                new MukuruIdempotencyCache(new InMemoryBhenguDistributedCache())));
    }

    [Fact]
    public void Constructor_Throws_WhenClientSecretMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new MukuruPaymentProvider(http, Options.Create(new MukuruOptions { ClientId = "x" }),
                NullLogger<MukuruPaymentProvider>.Instance,
                new MukuruIdempotencyCache(new InMemoryBhenguDistributedCache())));
    }

    [Fact]
    public void ProviderName_IsMukuru()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("mukuru", provider.ProviderName);
    }

    [Fact]
    public void Capabilities_IncludeMandatesAndTypedWebhooks()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Mandates));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.TypedWebhooks));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Idempotency));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Payout));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.CrossBorder));
        Assert.False(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.ThreeDSecure));
        Assert.False(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Tokenisation));
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsResponse_OnSuccess()
    {
        var handler = HandlerWithTokenAnd(req =>
        {
            Assert.Contains("v1/wallet/topup", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"transaction_id":"topup_123","status":"completed","reference":"topup-ref-1","message":"OK"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("topup_123", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Equal(2500m, response.Amount);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = HandlerWithTokenAnd(_ => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "throttle"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = HandlerWithTokenAnd(_ => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = HandlerWithTokenAnd(_ => StubHttpMessageHandler.Text(HttpStatusCode.InternalServerError, "boom"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("net"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_DedupesOnSameIdempotencyKey()
    {
        var calls = 0;
        var handler = HandlerWithTokenAnd(_ =>
        {
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                $$"""{"transaction_id":"topup_{{calls}}","status":"completed"}""");
        });
        var provider = Create(handler);
        var r1 = await provider.ProcessPaymentAsync(SamplePayment() with { IdempotencyKey = "k1" });
        var r2 = await provider.ProcessPaymentAsync(SamplePayment() with { IdempotencyKey = "k1" });
        Assert.Equal(r1.GatewayReference, r2.GatewayReference);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = HandlerWithTokenAnd(req =>
        {
            Assert.Contains("v1/transactions", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"transaction_id":"tx_zw_001","status":"pending","reference":"mukuru-abc"}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "ZW:CASH_PICKUP:",
            Amount = 1500m,
            Currency = "USD",
            Description = "Family support"
        });

        Assert.Equal("tx_zw_001", payout.GatewayReference);
        Assert.Equal(1500m, payout.Amount);
        Assert.Equal(PaymentStatus.Pending, payout.Status);
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws_OnMalformedDestination()
    {
        var handler = HandlerWithTokenAnd(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            provider.ProcessPayoutAsync(new PayoutRequest
            {
                DestinationToken = "ZW",
                Amount = 100m,
                Currency = "USD",
                Description = "x"
            }));
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsResponse_OnSuccess()
    {
        var handler = HandlerWithTokenAnd(req =>
        {
            Assert.Contains("/cancel", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"transaction_id":"tx_001","status":"cancelled","message":"Cancelled before collection"}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "tx_001",
            Amount = 1500m,
            Reason = "Sender requested"
        });

        Assert.Equal("tx_001", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Cancelled, refund.Status);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenWebhookSecretMissing()
    {
        var provider = Create(
            new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)),
            new MukuruOptions { ClientId = "c", ClientSecret = "s", WebhookSecret = "" });
        Assert.False(provider.VerifyWebhookSignature("payload", "sig"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string secret = "webhook-mukuru-secret";
        const string payload = """{"event_type":"transaction.completed","data":{"transaction_id":"tx_1"}}""";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var validSig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(payload, validSig));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("anything", "0000beef"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsTypedPayoutCompleted_ForTransactionCompleted()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event_type":"transaction.completed","data":{"transaction_id":"tx_42","status":"collected","amount":"1500.00","currency":"USD","destination":"ZW:CASH:"}}
            """);
        Assert.NotNull(evt);
        var typed = Assert.IsType<PayoutCompletedEvent>(evt);
        Assert.Equal("tx_42", typed.GatewayReference);
        Assert.Equal(WebhookEventCategory.PayoutCompleted, typed.Category);
        Assert.Equal("ZW:CASH:", typed.DestinationToken);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsTypedMandateActivated()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event_type":"mandate.activated","data":{"transaction_id":"man_1","amount":"5000.00","currency":"ZAR"}}
            """);
        Assert.NotNull(evt);
        var typed = Assert.IsType<MandateActivatedEvent>(evt);
        Assert.Equal("man_1", typed.MandateReference);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownEventType()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("""
            {"event_type":"unknown","data":{"transaction_id":"x"}}
            """));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
