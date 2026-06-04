// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Stripe;

/// <summary>
/// Stripe tests now cover the full provider surface because the provider was refactored
/// to route Stripe.net through a <see cref="global::Stripe.StripeClient"/> built from the
/// injected <see cref="HttpClient"/>. Mocked <see cref="HttpMessageHandler"/> responses
/// intercept all outbound calls.
/// </summary>
[Collection(StripeConfigurationCollection.Name)]
public class StripePaymentProviderTests
{
    private static StripePaymentProvider Create(StubHttpMessageHandler handler, StripeOptions? opts = null)
    {
        opts ??= new StripeOptions { SecretKey = "sk_test_fake", WebhookSecret = "whsec_test_fake" };
        var http = new HttpClient(handler);
        return new StripePaymentProvider(http, Options.Create(opts), NullLogger<StripePaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "pm_card_visa",
        Amount = 99.99m,
        Currency = "ZAR",
        Description = "Stripe test"
    };

    // === Configuration tests ===

    [Fact]
    public void Constructor_Throws_WhenSecretKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        var ex = Assert.Throws<ProviderConfigurationException>(() =>
            new StripePaymentProvider(http, Options.Create(new StripeOptions()), NullLogger<StripePaymentProvider>.Instance));
        Assert.Equal("stripe", ex.ProviderName);
        Assert.Contains("SecretKey", ex.Message);
    }

    [Fact]
    public void Constructor_SetsStripeConfigurationApiKey()
    {
        // StripeConfiguration.ApiKey is process-wide mutable state shared by every Stripe provider
        // in this assembly. This test is in the StripeConfiguration xunit collection
        // (DisableParallelization = true), so every other Stripe-constructor-mutating test in that
        // collection serialises against this one — eliminating the read-modify-read race that made
        // this test flaky. We still snapshot+restore so we don't leak the fake key into the next
        // test that happens to run in the same serial slot.
        var snapshot = global::Stripe.StripeConfiguration.ApiKey;
        try
        {
            const string testKey = "sk_test_BHENGU_TEST_KEY";
            _ = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")),
                       new StripeOptions { SecretKey = testKey, WebhookSecret = "whsec_test_fake" });
            Assert.Equal(testKey, global::Stripe.StripeConfiguration.ApiKey);
        }
        finally
        {
            global::Stripe.StripeConfiguration.ApiKey = snapshot;
        }
    }

    [Fact]
    public void ProviderName_IsStripe()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("stripe", provider.ProviderName);
    }

    // === Payment tests (now exercising the real HTTP path via StripeClient) ===

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsCompletedResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("payment_intents", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"pi_test_123","object":"payment_intent","amount":9999,"currency":"zar","status":"succeeded","description":"Stripe test"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("pi_test_123", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Equal(99.99m, response.Amount);
        Assert.Equal("ZAR", response.Currency);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.PaymentRequired, """
            {"error":{"type":"card_error","code":"card_declined","message":"Your card was declined.","decline_code":"generic_decline"}}
            """));
        var provider = Create(handler);
        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
        Assert.Equal("card_declined", ex.ProviderErrorCode);
        Assert.Contains("declined", ex.ProviderErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.TooManyRequests, """
            {"error":{"type":"invalid_request_error","message":"Too many requests"}}
            """));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.BadGateway, """
            {"error":{"type":"api_error","message":"Stripe is down"}}
            """));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    // === Refund tests ===

    [Fact]
    public async Task ProcessRefundAsync_ReturnsRefundedResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("refunds", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"re_test_1","object":"refund","amount":5000,"currency":"zar","payment_intent":"pi_test_123","status":"succeeded","reason":"requested_by_customer"}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "pi_test_123",
            Amount = 50m,
            Reason = "Customer requested"
        });
        Assert.Equal("re_test_1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, refund.Status);
        Assert.Equal(50m, refund.Amount);
    }

    [Fact]
    public async Task ProcessRefundAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, """
            {"error":{"type":"invalid_request_error","code":"charge_already_refunded","message":"Charge already refunded"}}
            """));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            provider.ProcessRefundAsync(new RefundRequest
            {
                GatewayReference = "pi_x", Amount = 10m, Reason = "test"
            }));
    }

    // === Payout tests ===

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("payouts", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"po_test_1","object":"payout","amount":50000,"currency":"zar","status":"paid","destination":"ba_test_1"}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "ba_test_1",
            Amount = 500m,
            Currency = "ZAR",
            Description = "Vendor payout"
        });
        Assert.Equal("po_test_1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
        Assert.Equal(500m, payout.Amount);
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, """
            {"error":{"type":"invalid_request_error","code":"balance_insufficient","message":"Insufficient balance"}}
            """));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            provider.ProcessPayoutAsync(new PayoutRequest
            {
                DestinationToken = "ba_x", Amount = 1000m, Currency = "ZAR", Description = "p"
            }));
    }

    // === Webhook tests (pure functions, no HTTP) ===

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenWebhookSecretMissing()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)),
                              new StripeOptions { SecretKey = "sk_test", WebhookSecret = "" });
        Assert.False(provider.VerifyWebhookSignature("any-payload", "t=1,v1=anything"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForMalformedSignatureHeader()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("any-payload", "not-a-stripe-signature"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("{}", "t=1234567890,v1=deadbeef"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownEventType()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var payload = """
            {"id":"evt_test","object":"event","type":"some.unknown.type","data":{"object":{}}}
            """;
        Assert.Null(await provider.ParseWebhookAsync(payload));
    }
}
