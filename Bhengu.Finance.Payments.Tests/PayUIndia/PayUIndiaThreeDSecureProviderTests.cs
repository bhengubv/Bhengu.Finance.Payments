// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.ThreeDSecure;
using Bhengu.Finance.Payments.PayUIndia.Configuration;
using Bhengu.Finance.Payments.PayUIndia.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayUIndia;

public class PayUIndiaThreeDSecureProviderTests
{
    private static PayUIndiaThreeDSecureProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new PayUIndiaOptions
            {
                MerchantKey = "gtKFFx",
                Salt = "eCwWELxi",
                SuccessUrl = "https://merchant.example.com/success",
                FailureUrl = "https://merchant.example.com/failure"
            }),
            NullLogger<PayUIndiaThreeDSecureProvider>.Instance);

    private static PaymentRequest SampleIntent() => new()
    {
        PaymentMethodToken = "n/a",
        Amount = 100m,
        Currency = "INR",
        Description = "3DS test",
        Metadata = new Dictionary<string, string>
        {
            ["txnid"] = "txn3ds1",
            ["firstname"] = "Test",
            ["email"] = "buyer@example.com",
            ["bankcode"] = "VISA",
            ["ccnum"] = "4012001037141112",
            ["ccvv"] = "123",
            ["ccexpmon"] = "12",
            ["ccexpyr"] = "2030"
        }
    };

    [Fact]
    public async Task StartAuthenticationAsync_ChallengeRequired_ReturnsRedirect()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("_payment", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","mihPayId":"403993715","metaData":{"txnStatus":"redirect","postUri":"https://acs.example.com/auth","threeDsVersion":"2.2.0","dsTransactionId":"ds-1"}}
                """);
        });
        var provider = Create(handler);

        var result = await provider.StartAuthenticationAsync(SampleIntent());

        Assert.Equal(ThreeDSecureStatus.ChallengeRequired, result.Status);
        Assert.Equal("403993715", result.ChallengeReference);
        Assert.Equal("https://acs.example.com/auth", result.RedirectUrl);
        Assert.Equal("2.2.0", result.ProtocolVersion);
        Assert.Equal("ds-1", result.DsTransactionId);
    }

    [Fact]
    public async Task StartAuthenticationAsync_Frictionless_ReturnsAuthenticated()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","mihPayId":"403993716","metaData":{"txnStatus":"success"}}
                """));
        var provider = Create(handler);

        var result = await provider.StartAuthenticationAsync(SampleIntent());
        Assert.Equal(ThreeDSecureStatus.Authenticated, result.Status);
        Assert.Equal("403993716", result.ChallengeReference);
    }

    [Fact]
    public async Task GetChallengeAsync_PendingMapped_AsChallengeRequired()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("merchant/postservice.php", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"pending"}
                """);
        });
        var provider = Create(handler);

        var result = await provider.GetChallengeAsync("403993715");

        Assert.Equal(ThreeDSecureStatus.ChallengeRequired, result.Status);
        Assert.Equal("403993715", result.ChallengeReference);
    }

    [Fact]
    public async Task GetChallengeAsync_SuccessMapped_AsAuthenticated()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success"}
                """));
        var provider = Create(handler);
        var result = await provider.GetChallengeAsync("403993715");
        Assert.Equal(ThreeDSecureStatus.Authenticated, result.Status);
    }

    [Fact]
    public async Task StartAuthenticationAsync_OnRateLimit_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "throttle"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.StartAuthenticationAsync(SampleIntent()));
    }

    [Fact]
    public async Task StartAuthenticationAsync_OnNetworkFailure_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.StartAuthenticationAsync(SampleIntent()));
    }

    [Fact]
    public void Constructor_Throws_WhenMerchantKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new PayUIndiaThreeDSecureProvider(http, Options.Create(new PayUIndiaOptions { Salt = "x" }),
                NullLogger<PayUIndiaThreeDSecureProvider>.Instance));
    }
}
