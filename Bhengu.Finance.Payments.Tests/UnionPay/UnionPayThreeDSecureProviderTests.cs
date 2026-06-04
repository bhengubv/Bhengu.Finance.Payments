// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.ThreeDSecure;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.UnionPay.Configuration;
using Bhengu.Finance.Payments.UnionPay.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.UnionPay;

public class UnionPayThreeDSecureProviderTests
{
    private static readonly RSA SharedRsa = RSA.Create(2048);
    private static readonly string PrivateKeyPem = Convert.ToBase64String(SharedRsa.ExportPkcs8PrivateKey());

    private static UnionPayThreeDSecureProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new UnionPayOptions
            {
                MerId = "777290058110097",
                CertId = "68759585097",
                SignCertPrivateKey = PrivateKeyPem,
                FrontUrl = "https://example.com/return",
                BackUrl = "https://example.com/notify",
                Currency = "156",
                Encoding = "UTF-8",
                UseSandbox = true
            }),
            NullLogger<UnionPayThreeDSecureProvider>.Instance);

    private static PaymentRequest SampleIntent() => new()
    {
        PaymentMethodToken = "ORD3DS001",
        Amount = 100m,
        Currency = "156",
        Description = "UnionPay 3DS"
    };

    [Fact]
    public async Task StartAuthenticationAsync_ReturnsChallengeRequiredRedirect()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("Should not POST"));
        var provider = Create(handler);
        var challenge = await provider.StartAuthenticationAsync(SampleIntent());

        Assert.Equal(ThreeDSecureStatus.ChallengeRequired, challenge.Status);
        Assert.Equal("ORD3DS001", challenge.ChallengeReference);
        Assert.NotNull(challenge.RedirectUrl);
        Assert.Contains("frontTransReq.do", challenge.RedirectUrl);
        Assert.Contains("threeDSecure=1", challenge.RedirectUrl);
    }

    [Fact]
    public async Task GetChallengeAsync_AuthenticatedRespCode_ReturnsAuthenticated()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("queryTrans.do", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Text(HttpStatusCode.OK, "respCode=00&origRespCode=00&eci=05&cavv=AAAA");
        });
        var provider = Create(handler);
        var result = await provider.GetChallengeAsync("ORD3DS001");
        Assert.Equal(ThreeDSecureStatus.Authenticated, result.Status);
        Assert.Equal("AAAA", result.ChallengePayload);
    }

    [Fact]
    public async Task GetChallengeAsync_PendingOrigRespCode_ReturnsChallengeRequired()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.OK, "respCode=00&origRespCode=03"));
        var provider = Create(handler);
        var result = await provider.GetChallengeAsync("ORD3DS001");
        Assert.Equal(ThreeDSecureStatus.ChallengeRequired, result.Status);
    }

    [Fact]
    public async Task GetChallengeAsync_FailedRespCode_ReturnsFailed()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.OK, "respCode=99&origRespCode=99"));
        var provider = Create(handler);
        var result = await provider.GetChallengeAsync("ORD3DS001");
        Assert.Equal(ThreeDSecureStatus.Failed, result.Status);
    }

    [Fact]
    public async Task GetChallengeAsync_OnRateLimit_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "throttle"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.GetChallengeAsync("ORD3DS001"));
    }

    [Fact]
    public async Task GetChallengeAsync_OnNetworkFailure_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.GetChallengeAsync("ORD3DS001"));
    }
}
