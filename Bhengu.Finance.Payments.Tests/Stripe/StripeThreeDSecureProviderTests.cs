// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.ThreeDSecure;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Stripe;

[Collection(StripeConfigurationCollection.Name)]
public class StripeThreeDSecureProviderTests
{
    private static StripeThreeDSecureProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new StripeOptions { SecretKey = "sk_test_fake", WebhookSecret = "whsec_test_fake" }),
            NullLogger<StripeThreeDSecureProvider>.Instance);

    private static PaymentRequest SampleCharge() => new()
    {
        PaymentMethodToken = "pm_card_threeDSecure2Required",
        Amount = 50m,
        Currency = "USD",
        Description = "3DS test"
    };

    [Fact]
    public async Task StartAuthenticationAsync_ChallengeRequired_ReturnsRedirectUrl()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("payment_intents", req.RequestUri!.PathAndQuery, StringComparison.Ordinal);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"pi_3ds_1","object":"payment_intent","amount":5000,"currency":"usd","status":"requires_action","client_secret":"pi_3ds_1_secret_xxx","next_action":{"type":"redirect_to_url","redirect_to_url":{"url":"https://hooks.stripe.com/redirect/authenticate/src_xxx","return_url":"https://example.com/return"}}}
                """);
        });
        var provider = Create(handler);
        var challenge = await provider.StartAuthenticationAsync(SampleCharge());

        Assert.Equal(ThreeDSecureStatus.ChallengeRequired, challenge.Status);
        Assert.Equal("pi_3ds_1", challenge.ChallengeReference);
        Assert.Equal("https://hooks.stripe.com/redirect/authenticate/src_xxx", challenge.RedirectUrl);
        Assert.Equal("pi_3ds_1_secret_xxx", challenge.ChallengePayload);
    }

    [Fact]
    public async Task StartAuthenticationAsync_Frictionless_ReturnsAuthenticated()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"id":"pi_3ds_2","object":"payment_intent","amount":5000,"currency":"usd","status":"succeeded","client_secret":"pi_3ds_2_secret_yyy"}
            """));
        var provider = Create(handler);
        var challenge = await provider.StartAuthenticationAsync(SampleCharge());
        Assert.Equal(ThreeDSecureStatus.Authenticated, challenge.Status);
        Assert.Null(challenge.RedirectUrl);
    }

    [Fact]
    public async Task StartAuthenticationAsync_Throws4xxAsPaymentDeclined()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.PaymentRequired, """
            {"error":{"type":"card_error","code":"authentication_required","message":"3DS authentication required"}}
            """));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.StartAuthenticationAsync(SampleCharge()));
    }

    [Fact]
    public async Task StartAuthenticationAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.TooManyRequests, """
            {"error":{"type":"invalid_request_error","message":"Too many requests"}}
            """));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.StartAuthenticationAsync(SampleCharge()));
    }

    [Fact]
    public async Task StartAuthenticationAsync_Throws5xxAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.BadGateway, """
            {"error":{"type":"api_error","message":"Stripe is down"}}
            """));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.StartAuthenticationAsync(SampleCharge()));
    }

    [Fact]
    public async Task GetChallengeAsync_ReturnsCurrentStatus()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"pi_3ds_3","object":"payment_intent","amount":5000,"currency":"usd","status":"succeeded","client_secret":"pi_3ds_3_secret_zzz"}
                """);
        });
        var provider = Create(handler);
        var challenge = await provider.GetChallengeAsync("pi_3ds_3");
        Assert.Equal(ThreeDSecureStatus.Authenticated, challenge.Status);
    }
}
