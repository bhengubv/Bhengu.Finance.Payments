// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Text;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Onafriq.Configuration;
using Bhengu.Finance.Payments.Onafriq.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Onafriq;

/// <summary>
/// Onafriq's Portal API is the Beyonic API (rebranded). These tests assert the real wire shape:
/// Token auth, POST /collectionrequests (payin) and POST /payments (disbursement) with form-encoded
/// bodies, and the { hook, data } webhook envelope verified via optional HTTP Basic Auth.
/// </summary>
public class OnafriqPaymentProviderTests
{
    private static OnafriqPaymentProvider Create(StubHttpMessageHandler handler, OnafriqOptions? opts = null, IBhenguDistributedCache? cache = null)
    {
        opts ??= new OnafriqOptions
        {
            ApiKey = "onafriq_test_key",
            CallbackUrl = "https://example.com/cb",
            WebhookBasicAuthUsername = "hookuser",
            WebhookBasicAuthPassword = "hookpass"
        };
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.mfsafrica.com/api/") };
        return new OnafriqPaymentProvider(http, Options.Create(opts), NullLogger<OnafriqPaymentProvider>.Instance, cache);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "+254727447101",
        Amount = 200m,
        Currency = "KES",
        Description = "Onafriq collection test"
    };

    [Fact]
    public void Constructor_Throws_WhenApiKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new OnafriqPaymentProvider(http, Options.Create(new OnafriqOptions()), NullLogger<OnafriqPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_SetsTokenAuthHeader()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        _ = new OnafriqPaymentProvider(http, Options.Create(new OnafriqOptions { ApiKey = "k" }), NullLogger<OnafriqPaymentProvider>.Instance);
        Assert.Equal("Token", http.DefaultRequestHeaders.Authorization!.Scheme);
        Assert.Equal("k", http.DefaultRequestHeaders.Authorization!.Parameter);
    }

    [Fact]
    public void ProviderName_IsOnafriq()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("onafriq", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_PostsCollectionRequest_AndReturnsResponse()
    {
        string? capturedBody = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("collectionrequests", req.RequestUri!.PathAndQuery);
            Assert.Equal("Token", req.Headers.Authorization?.Scheme);
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":80123,"status":"pending","amount":200,"currency":"KES","phonenumber":"+254727447101"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("80123", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal(200m, response.Amount);
        // Form-encoded body carries the documented Beyonic fields.
        Assert.Contains("phonenumber=%2B254727447101", capturedBody);
        Assert.Contains("currency=KES", capturedBody);
        Assert.Contains("amount=200", capturedBody);
    }

    [Fact]
    public async Task ProcessPaymentAsync_MapsSuccessfulStatus_ToCompleted()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"id":1,"status":"successful","amount":200,"currency":"KES"}
            """));
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());
        Assert.Equal(PaymentStatus.Completed, response.Status);
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
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "invalid number"));
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
    public async Task ProcessPayoutAsync_PostsPayment_AndReturnsResponse()
    {
        string? capturedBody = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("payments", req.RequestUri!.PathAndQuery);
            Assert.DoesNotContain("collectionrequests", req.RequestUri!.PathAndQuery);
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":99001,"state":"new","amount":100,"currency":"GHS"}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "+233244000000",
            Amount = 100m,
            Currency = "GHS",
            Description = "Cross-border payout"
        });

        Assert.Equal("99001", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, payout.Status); // "new" => Pending
        Assert.Contains("payment_type=money", capturedBody);
        Assert.Contains("phonenumber=%2B233244000000", capturedBody);
    }

    [Fact]
    public async Task ProcessPayoutAsync_MapsProcessedState_ToCompleted()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"id":2,"state":"processed","amount":100,"currency":"GHS"}
            """));
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "+233244000000",
            Amount = 100m,
            Currency = "GHS",
            Description = "p"
        });
        Assert.Equal(PaymentStatus.Completed, payout.Status);
    }

    [Fact]
    public async Task ProcessRefundAsync_Throws_WithExplanation()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "txn_x",
            Amount = 10m,
            Reason = "Customer requested"
        }));
        Assert.Equal("refund_unsupported", ex.ProviderErrorCode);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenBasicAuthNotConfigured()
    {
        var provider = Create(
            new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)),
            new OnafriqOptions { ApiKey = "k" });
        Assert.False(provider.VerifyWebhookSignature("payload", "Basic abc"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForMatchingBasicAuthHeader()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("hookuser:hookpass"));
        Assert.True(provider.VerifyWebhookSignature("""{"hook":{"event":"x"}}""", header));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForBareBase64Credential()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var bare = Convert.ToBase64String(Encoding.UTF8.GetBytes("hookuser:hookpass"));
        Assert.True(provider.VerifyWebhookSignature("payload", bare));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForWrongCredential()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("hookuser:WRONG"));
        Assert.False(provider.VerifyWebhookSignature("payload", header));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsPayoutCompleted_ForPaymentProcessed()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"hook":{"id":53,"event":"payment.status.changed","target":"https://cb/"},
             "data":{"id":99001,"state":"processed","amount":100,"currency":"GHS","phonenumber":"+233244000000"}}
            """);
        var typed = Assert.IsType<PayoutCompletedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutCompleted, typed.Category);
        Assert.Equal("99001", typed.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, typed.Status);
        Assert.Equal("+233244000000", typed.DestinationToken);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsPayoutFailed_ForPaymentRejected()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"hook":{"event":"payment.status.changed"},
             "data":{"id":7,"state":"rejected","amount":50,"currency":"UGX","last_error":"no such contact"}}
            """);
        var typed = Assert.IsType<PayoutFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.PayoutFailed, typed.Category);
        Assert.Equal(PaymentStatus.Failed, typed.Status);
        Assert.Equal("no such contact", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargeSucceeded_ForCollectionSuccessful()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"hook":{"event":"collectionrequest.status.changed"},
             "data":{"id":80123,"status":"successful","amount":200,"currency":"KES","phonenumber":"+254727447101"}}
            """);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal("80123", typed.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, typed.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsChargePending_ForCollectionPending()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"hook":{"event":"collectionrequest.status.changed"},
             "data":{"id":1,"status":"pending","amount":10,"currency":"KES"}}
            """);
        var typed = Assert.IsType<ChargePendingEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargePending, typed.Category);
        Assert.Equal(PaymentStatus.Pending, typed.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownEventType()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("""
            {"hook":{"event":"contact.created"},"data":{"id":26}}
            """));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }

    [Fact]
    public async Task ProcessPayoutAsync_DedupesViaIdempotencyKey()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":123,"state":"new"}
                """);
        });
        var cache = new InMemoryBhenguDistributedCache();
        var provider = Create(handler, cache: cache);
        var req = new PayoutRequest
        {
            DestinationToken = "+233244000000",
            Amount = 100m,
            Currency = "GHS",
            Description = "test",
            IdempotencyKey = "idem-1"
        };
        var first = await provider.ProcessPayoutAsync(req);
        var second = await provider.ProcessPayoutAsync(req);
        Assert.Equal(first.GatewayReference, second.GatewayReference);
        Assert.Equal(1, calls);
    }
}
