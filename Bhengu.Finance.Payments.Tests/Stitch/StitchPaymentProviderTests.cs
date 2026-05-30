// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Stitch.Configuration;
using Bhengu.Finance.Payments.Stitch.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Stitch;

public class StitchPaymentProviderTests
{
    private static StitchPaymentProvider Create(StubHttpMessageHandler handler, StitchOptions? opts = null)
    {
        opts ??= new StitchOptions
        {
            ClientId = "stitch-client",
            ApiKey = "stitch-key",
            WebhookSecret = "webhook-stitch-secret",
            BeneficiaryAccountNumber = "1234567890",
            BeneficiaryBankId = "fnb",
            BeneficiaryName = "Bhengu Merchant",
            Currency = "ZAR"
        };
        var http = new HttpClient(handler);
        return new StitchPaymentProvider(http, Options.Create(opts), NullLogger<StitchPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "ext-ref-1",
        Amount = 1500m,
        Currency = "ZAR",
        Description = "Stitch test"
    };

    [Fact]
    public void Constructor_Throws_WhenClientIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new StitchPaymentProvider(http, Options.Create(new StitchOptions { ApiKey = "k" }),
                NullLogger<StitchPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenNoAuthConfigured()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new StitchPaymentProvider(http, Options.Create(new StitchOptions { ClientId = "c" }),
                NullLogger<StitchPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsStitch()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("stitch", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsPendingResponse_OnGraphqlSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("graphql", req.RequestUri!.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":{"clientPaymentInitiationRequestCreate":{"paymentInitiationRequest":{"id":"pir_001","url":"https://secure.stitch.money/pay/pir_001"}}}}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("pir_001", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal("https://secure.stitch.money/pay/pir_001", response.RedirectUrl);
        Assert.Equal("Pay-by-bank initiated", response.Message);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "throttled"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad query"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.ServiceUnavailable, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("/refund", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"rf_stitch_1","status":"completed"}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "pir_001",
            Amount = 500m,
            Reason = "Goods not delivered"
        });

        Assert.Equal("rf_stitch_1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, refund.Status);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnGraphqlSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("graphql", req.RequestUri!.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":{"clientPayoutInitiationRequestCreate":{"payoutInitiationRequest":{"id":"por_001","status":"submitted"}}}}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "absa:0123456789:Jane Beneficiary",
            Amount = 750m,
            Currency = "ZAR",
            Description = "Supplier payout"
        });

        Assert.Equal("por_001", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, payout.Status);
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws_OnMalformedDestination()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            provider.ProcessPayoutAsync(new PayoutRequest
            {
                DestinationToken = "absa:only-two-parts",
                Amount = 100m,
                Currency = "ZAR",
                Description = "x"
            }));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string secret = "webhook-stitch-secret";
        const string payload = """{"eventType":"paymentInitiationRequest.completed","data":{"id":"pir_001"}}""";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var validSig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(payload, validSig));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("anything", "feedface"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenWebhookSecretMissing()
    {
        var provider = Create(
            new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)),
            new StitchOptions
            {
                ClientId = "c",
                ApiKey = "k",
                WebhookSecret = "",
                BeneficiaryAccountNumber = "1",
                BeneficiaryBankId = "fnb",
                BeneficiaryName = "x"
            });
        Assert.False(provider.VerifyWebhookSignature("payload", "sig"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForPaymentCompleted()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"eventType":"paymentInitiationRequest.completed","data":{"id":"pir_42","status":"completed"}}
            """);
        Assert.NotNull(evt);
        Assert.Equal("pir_42", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownEventType()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("""
            {"eventType":"unrelated.thing","data":{"id":"x"}}
            """));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
