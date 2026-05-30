// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Stripe;

/// <summary>
/// Stripe tests cover what's reachable without a Stripe sandbox account: constructor /
/// configuration paths and the parts of webhook handling that go through Stripe.net's
/// pure functions (<c>EventUtility.ConstructEvent</c> and <c>EventUtility.ParseEvent</c>).
///
/// ProcessPaymentAsync / ProcessRefundAsync / ProcessPayoutAsync exercise the Stripe.net
/// service classes, which ignore the injected HttpClient and use their own internal client.
/// End-to-end coverage of those paths belongs in IntegrationTests against the Stripe sandbox.
/// </summary>
public class StripePaymentProviderTests
{
    private static StripePaymentProvider CreateProvider(StripeOptions? opts = null)
    {
        opts ??= new StripeOptions { SecretKey = "sk_test_fake", WebhookSecret = "whsec_test_fake" };
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(System.Net.HttpStatusCode.OK, "{}")));
        return new StripePaymentProvider(http, Options.Create(opts), NullLogger<StripePaymentProvider>.Instance);
    }

    [Fact]
    public void Constructor_Throws_WhenSecretKeyMissing()
    {
        var http = new HttpClient();
        var ex = Assert.Throws<ProviderConfigurationException>(() =>
            new StripePaymentProvider(http, Options.Create(new StripeOptions()), NullLogger<StripePaymentProvider>.Instance));
        Assert.Equal("stripe", ex.ProviderName);
        Assert.Contains("SecretKey", ex.Message);
    }

    [Fact]
    public void Constructor_SetsStripeConfigurationApiKey()
    {
        _ = CreateProvider(new StripeOptions { SecretKey = "sk_test_BHENGU_TEST_KEY" });
        Assert.Equal("sk_test_BHENGU_TEST_KEY", global::Stripe.StripeConfiguration.ApiKey);
    }

    [Fact]
    public void ProviderName_IsStripe()
    {
        var provider = CreateProvider();
        Assert.Equal("stripe", provider.ProviderName);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenWebhookSecretMissing()
    {
        var provider = CreateProvider(new StripeOptions { SecretKey = "sk_test", WebhookSecret = "" });
        Assert.False(provider.VerifyWebhookSignature("any-payload", "t=1,v1=anything"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForMalformedSignatureHeader()
    {
        var provider = CreateProvider();
        // Stripe.net throws on malformed signature header; our wrapper catches and returns false.
        Assert.False(provider.VerifyWebhookSignature("any-payload", "not-a-stripe-signature"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = CreateProvider();
        Assert.False(provider.VerifyWebhookSignature("{}", "t=1234567890,v1=deadbeef"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = CreateProvider();
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownEventType()
    {
        var provider = CreateProvider();
        var payload = """
            {"id":"evt_test","object":"event","type":"some.unknown.type","data":{"object":{}}}
            """;
        // The Stripe parser will accept this — our switch returns null for unknown types.
        var result = await provider.ParseWebhookAsync(payload);
        Assert.Null(result);
    }
}
