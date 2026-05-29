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
/// Stripe tests are deliberately limited to constructor / configuration paths.
/// The Stripe.net SDK ignores the injected HttpClient and uses its own internal client,
/// so end-to-end HTTP stubbing is not feasible without a deeper Stripe.net wiring.
/// Real integration tests for Stripe belong in IntegrationTests against the Stripe sandbox.
/// </summary>
public class StripePaymentProviderTests
{
    private static StripePaymentProvider CreateProvider(StripeOptions? opts = null)
    {
        opts ??= new StripeOptions { SecretKey = "sk_test_fake" };
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
}
