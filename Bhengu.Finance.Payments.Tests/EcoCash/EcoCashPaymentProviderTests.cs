// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.EcoCash.Configuration;
using Bhengu.Finance.Payments.EcoCash.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.EcoCash;

/// <summary>
/// Tests for the EcoCash Open API (developers.ecocash.co.zw) adapter: C2B charge + refund over
/// X-API-KEY auth. The provider exposes no payout (no documented B2C endpoint) and no signed webhook,
/// so those surfaces are asserted as honest no-ops.
/// </summary>
public class EcoCashPaymentProviderTests
{
    private static EcoCashPaymentProvider Create(StubHttpMessageHandler handler, EcoCashOptions? opts = null, IBhenguDistributedCache? cache = null)
    {
        opts ??= new EcoCashOptions
        {
            ApiKey = "key_test",
            UseSandbox = true
        };
        var http = new HttpClient(handler);
        return new EcoCashPaymentProvider(http, Options.Create(opts), NullLogger<EcoCashPaymentProvider>.Instance, cache);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "263772111222",
        Amount = 10m,
        Currency = "USD",
        Description = "EcoCash test"
    };

    private static string Body(HttpRequestMessage req) => req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

    [Fact]
    public void Constructor_Throws_WhenApiKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new EcoCashPaymentProvider(http, Options.Create(new EcoCashOptions()), NullLogger<EcoCashPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsEcoCash()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("ecocash", provider.ProviderName);
    }

    [Fact]
    public void Capabilities_AreChargeRefund_NoPayoutNoWebhook()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.Charge));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.Refund));
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.MobileMoney));
        Assert.False(provider.Capabilities.HasFlag(ProviderCapabilities.Payout));
        Assert.False(provider.Capabilities.HasFlag(ProviderCapabilities.Webhook));
    }

    [Fact]
    public async Task ProcessPaymentAsync_PostsToC2bSandboxPath_WithVerifiedFields_AndApiKeyHeader()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            // Verified Open API path: /api/v2/payment/instant/c2b/{sandbox|live}
            Assert.Contains("api/v2/payment/instant/c2b/sandbox", req.RequestUri!.ToString());
            Assert.True(req.Headers.TryGetValues("X-API-KEY", out var keys));
            Assert.Equal("key_test", Assert.Single(keys!));

            var body = Body(req);
            // Verified Open API charge field names.
            Assert.Contains("\"customerMsisdn\":\"263772111222\"", body);
            Assert.Contains("\"sourceReference\":", body);
            Assert.Contains("\"currency\":\"USD\"", body);
            Assert.Contains("\"reason\":", body);

            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"Completed","ecocashReference":"EC123","customerMsisdn":"263772111222"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("EC123", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
    }

    [Fact]
    public async Task ProcessPaymentAsync_TreatsEmpty2xxBodyAsPending()
    {
        // The charge success-response body is undocumented; a bare 2xx is accepted as Pending.
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, ""));
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.False(string.IsNullOrEmpty(response.GatewayReference));
    }

    [Fact]
    public async Task ProcessPaymentAsync_UsesLivePath_WhenNotSandbox()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("api/v2/payment/instant/c2b/live", req.RequestUri!.ToString());
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"Completed","ecocashReference":"EC-L"}""");
        });
        var provider = Create(handler, new EcoCashOptions { ApiKey = "key_test", UseSandbox = false });
        var response = await provider.ProcessPaymentAsync(SamplePayment());
        Assert.Equal("EC-L", response.GatewayReference);
    }

    [Fact]
    public async Task ProcessPaymentAsync_MissingMsisdn_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "",
            Amount = 1m,
            Currency = "USD",
            Description = "x"
        }));
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
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.InternalServerError, "down"));
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
    public async Task ProcessRefundAsync_PostsToRefundPath_WithVerifiedMisspelledFields()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("api/v2/refund/instant/c2b/sandbox", req.RequestUri!.ToString());
            var body = Body(req);
            // The EcoCash Open API refund keys are spelled "origional" / "Corelator" on the wire.
            Assert.Contains("\"origionalEcocashTransactionReference\":\"EC123\"", body);
            Assert.Contains("\"refundCorelator\":", body);
            Assert.Contains("\"reasonForRefund\":\"Customer requested\"", body);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"transactionStatus":"Refunded","ecocashReference":"EC_REFUND_1"}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "EC123",
            Amount = 10m,
            Reason = "Customer requested"
        });

        Assert.Equal("EC_REFUND_1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_OpenApiHasNoSignedWebhook()
    {
        // The EcoCash Open API exposes no signed webhook; verification is impossible and returns false.
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("payload", "any-non-empty"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_OpenApiHasNoWebhookContract()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("""{"status":"Completed"}"""));
    }

    [Fact]
    public async Task ProcessPaymentAsync_DedupesViaIdempotencyKey()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"Completed","ecocashReference":"EC-1"}
                """);
        });
        var cache = new InMemoryBhenguDistributedCache();
        var provider = Create(handler, cache: cache);
        var req = new PaymentRequest
        {
            PaymentMethodToken = "263772111222",
            Amount = 1m,
            Currency = "USD",
            Description = "x",
            IdempotencyKey = "idem-1"
        };
        var first = await provider.ProcessPaymentAsync(req);
        var second = await provider.ProcessPaymentAsync(req);
        Assert.Equal(first.GatewayReference, second.GatewayReference);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReusesIdempotencyKeyAsSourceReference()
    {
        string? sentRef = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var body = Body(req);
            // sourceReference should echo the caller's IdempotencyKey for native retry-mapping.
            sentRef = body.Contains("\"sourceReference\":\"idem-xyz\"") ? "idem-xyz" : null;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"Pending","ecocashReference":"EC-2"}""");
        });
        var provider = Create(handler);
        await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "263772111222",
            Amount = 5m,
            Currency = "USD",
            Description = "x",
            IdempotencyKey = "idem-xyz"
        });
        Assert.Equal("idem-xyz", sentRef);
    }
}
