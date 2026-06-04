// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.ThreeDSecure;
using Bhengu.Finance.Payments.Kashier.Configuration;
using Bhengu.Finance.Payments.Kashier.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Kashier;

public class KashierThreeDSecureProviderTests
{
    private static KashierThreeDSecureProvider Create(StubHttpMessageHandler handler, KashierOptions? opts = null)
    {
        opts ??= new KashierOptions { ApiKey = "k", MerchantId = "MID", Currency = "EGP" };
        var http = new HttpClient(handler);
        return new KashierThreeDSecureProvider(http, Options.Create(opts), NullLogger<KashierThreeDSecureProvider>.Instance);
    }

    private static PaymentRequest SampleIntent(IReadOnlyDictionary<string, string>? extra = null) => new()
    {
        PaymentMethodToken = "tok",
        Amount = 100m,
        Currency = "EGP",
        Description = "3DS",
        Metadata = extra
    };

    [Fact]
    public async Task StartAuthenticationAsync_ReturnsChallengeRequired_WhenAcsUrlReturned()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"status":"SUCCESS","response":{"transactionId":"TX1","status":"PENDING_3DS","acsUrl":"https://issuer.example/auth","pareq":"PaReq","dsTransactionId":"DS-1","protocolVersion":"2.2.0"}}"""));
        var provider = Create(handler);
        var ch = await provider.StartAuthenticationAsync(SampleIntent());
        Assert.Equal(ThreeDSecureStatus.ChallengeRequired, ch.Status);
        Assert.Equal("TX1", ch.ChallengeReference);
        Assert.Equal("https://issuer.example/auth", ch.RedirectUrl);
        Assert.Equal("PaReq", ch.ChallengePayload);
        Assert.Equal("DS-1", ch.DsTransactionId);
    }

    [Fact]
    public async Task StartAuthenticationAsync_ReturnsAuthenticated_OnFrictionlessSuccess()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"status":"SUCCESS","response":{"transactionId":"TX2","status":"SUCCESS"}}"""));
        var provider = Create(handler);
        var ch = await provider.StartAuthenticationAsync(SampleIntent());
        Assert.Equal(ThreeDSecureStatus.Authenticated, ch.Status);
    }

    [Fact]
    public async Task StartAuthenticationAsync_ReturnsNotRequired_WhenOptedOut()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        var ch = await provider.StartAuthenticationAsync(SampleIntent(new Dictionary<string, string> { ["request_3d_secure"] = "false" }));
        Assert.Equal(ThreeDSecureStatus.NotRequired, ch.Status);
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
    public async Task GetChallengeAsync_ReturnsAuthenticated_WhenStatusSuccess()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"status":"SUCCESS","response":{"status":"SUCCESS","dsTransactionId":"DS-7","protocolVersion":"2.2.0"}}"""));
        var provider = Create(handler);
        var ch = await provider.GetChallengeAsync("TX1");
        Assert.Equal(ThreeDSecureStatus.Authenticated, ch.Status);
        Assert.Equal("DS-7", ch.DsTransactionId);
    }

    [Fact]
    public async Task GetChallengeAsync_Returns404AsChallengeRequired()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "no"));
        var provider = Create(handler);
        var ch = await provider.GetChallengeAsync("TX1");
        Assert.Equal(ThreeDSecureStatus.ChallengeRequired, ch.Status);
    }
}
