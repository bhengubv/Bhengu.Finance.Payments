// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.ThreeDSecure;
using Bhengu.Finance.Payments.Paymob.Configuration;
using Bhengu.Finance.Payments.Paymob.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paymob;

public class PaymobThreeDSecureProviderTests
{
    private static PaymobThreeDSecureProvider Create(StubHttpMessageHandler handler, PaymobOptions? opts = null)
    {
        opts ??= new PaymobOptions { ApiKey = "api_test_key", IntegrationId = 100, IframeId = 9, Currency = "EGP" };
        var http = new HttpClient(handler);
        return new PaymobThreeDSecureProvider(http, Options.Create(opts), NullLogger<PaymobThreeDSecureProvider>.Instance);
    }

    private static PaymentRequest SampleIntent(IReadOnlyDictionary<string, string>? extra = null) => new()
    {
        PaymentMethodToken = "tok",
        Amount = 100m,
        Currency = "EGP",
        Description = "3DS step-up",
        Metadata = extra ?? new Dictionary<string, string> { ["email"] = "buyer@example.com" }
    };

    [Fact]
    public async Task StartAuthenticationAsync_ReturnsChallengeRequired_WithRedirectAndPayload()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var p = req.RequestUri!.PathAndQuery;
            if (p.Contains("auth/tokens", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"auth_tok"}""");
            if (p.Contains("ecommerce/orders", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":4242}""");
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"pay_key_3ds"}""");
        });
        var provider = Create(handler);
        var ch = await provider.StartAuthenticationAsync(SampleIntent());
        Assert.Equal(ThreeDSecureStatus.ChallengeRequired, ch.Status);
        Assert.Equal("4242", ch.ChallengeReference);
        Assert.NotNull(ch.RedirectUrl);
        Assert.Contains("payment_token=pay_key_3ds", ch.RedirectUrl);
        Assert.Equal("pay_key_3ds", ch.ChallengePayload);
    }

    [Fact]
    public async Task StartAuthenticationAsync_ReturnsNotRequired_WhenRequest3DSOptedOut()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        var ch = await provider.StartAuthenticationAsync(SampleIntent(new Dictionary<string, string>
        {
            ["request_3d_secure"] = "false"
        }));
        Assert.Equal(ThreeDSecureStatus.NotRequired, ch.Status);
    }

    [Fact]
    public async Task StartAuthenticationAsync_ThrowsPaymentDeclined_WhenIntegrationIdMissing()
    {
        var opts = new PaymobOptions { ApiKey = "k", IframeId = 9 };
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler, opts);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.StartAuthenticationAsync(SampleIntent()));
    }

    [Fact]
    public async Task StartAuthenticationAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.StartAuthenticationAsync(SampleIntent()));
    }

    [Fact]
    public async Task StartAuthenticationAsync_WrapsNetworkAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.StartAuthenticationAsync(SampleIntent()));
    }

    [Fact]
    public async Task GetChallengeAsync_ReturnsAuthenticated_WhenInquirySuccessAnd3DS()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("auth/tokens", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"auth_tok"}""");
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":5,"success":true,"is_3d_secure":true}""");
        });
        var provider = Create(handler);
        var ch = await provider.GetChallengeAsync("4242");
        Assert.Equal(ThreeDSecureStatus.Authenticated, ch.Status);
        Assert.Equal("4242", ch.ChallengeReference);
        Assert.Equal("5", ch.DsTransactionId);
    }

    [Fact]
    public async Task GetChallengeAsync_ReturnsFailed_WhenInquirySuccessFalse()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("auth/tokens", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"auth_tok"}""");
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":99,"success":false}""");
        });
        var provider = Create(handler);
        var ch = await provider.GetChallengeAsync("oid-99");
        Assert.Equal(ThreeDSecureStatus.Failed, ch.Status);
    }
}
