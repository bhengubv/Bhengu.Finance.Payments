// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.CMI.Configuration;
using Bhengu.Finance.Payments.CMI.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.ThreeDSecure;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.CMI;

public class CMIThreeDSecureProviderTests
{
    private static CMIThreeDSecureProvider Create(StubHttpMessageHandler handler, CMIOptions? opts = null)
    {
        opts ??= new CMIOptions
        {
            ClientId = "600",
            StoreKey = "store",
            ApiUser = "u",
            ApiPassword = "p",
            OkUrl = "https://m.example/ok",
            FailUrl = "https://m.example/fail",
            CallbackUrl = "https://m.example/cb",
            Currency = "504",
            Lang = "en",
            UseSandbox = true
        };
        var http = new HttpClient(handler);
        return new CMIThreeDSecureProvider(http, Options.Create(opts), NullLogger<CMIThreeDSecureProvider>.Instance);
    }

    private static PaymentRequest SampleIntent() => new()
    {
        PaymentMethodToken = "OID-1",
        Amount = 200m,
        Currency = "MAD",
        Description = "3DS test",
        Metadata = new Dictionary<string, string> { ["email"] = "buyer@example.com", ["BillToName"] = "Buyer", ["rnd"] = "r1" }
    };

    [Fact]
    public async Task StartAuthenticationAsync_AlwaysReturnsChallengeRequired_WithSignedRedirectUrl()
    {
        // CMI's 3DS is the hosted page — challenge is always required at start.
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var ch = await provider.StartAuthenticationAsync(SampleIntent());
        Assert.Equal(ThreeDSecureStatus.ChallengeRequired, ch.Status);
        Assert.Equal("OID-1", ch.ChallengeReference);
        Assert.NotNull(ch.RedirectUrl);
        Assert.Contains("est3Dgate", ch.RedirectUrl);
        Assert.Contains("hash=", ch.RedirectUrl);
        Assert.Contains("currency=504", ch.RedirectUrl);
    }

    [Fact]
    public async Task GetChallengeAsync_ReturnsAuthenticated_WhenApprovedAndMdStatusOne()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.OK,
            "<CC5Response><Response>Approved</Response><ProcReturnCode>00</ProcReturnCode><Extra><mdStatus>1</mdStatus></Extra></CC5Response>"));
        var provider = Create(handler);
        var ch = await provider.GetChallengeAsync("OID-1");
        Assert.Equal(ThreeDSecureStatus.Authenticated, ch.Status);
        Assert.Equal("00", ch.DsTransactionId);
    }

    [Fact]
    public async Task GetChallengeAsync_ReturnsAttempted_WhenMdStatusTwoToFour()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.OK,
            "<CC5Response><Response>Pending</Response><ProcReturnCode>00</ProcReturnCode><Extra><mdStatus>2</mdStatus></Extra></CC5Response>"));
        var provider = Create(handler);
        var ch = await provider.GetChallengeAsync("OID-1");
        Assert.Equal(ThreeDSecureStatus.Attempted, ch.Status);
    }

    [Fact]
    public async Task GetChallengeAsync_ReturnsFailed_OnDeclinedResponse()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.OK,
            "<CC5Response><Response>Declined</Response><ProcReturnCode>05</ProcReturnCode><Extra></Extra></CC5Response>"));
        var provider = Create(handler);
        var ch = await provider.GetChallengeAsync("OID-1");
        Assert.Equal(ThreeDSecureStatus.Failed, ch.Status);
    }

    [Fact]
    public async Task GetChallengeAsync_Returns4xxAsChallengeRequired()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        var ch = await provider.GetChallengeAsync("OID-1");
        Assert.Equal(ThreeDSecureStatus.ChallengeRequired, ch.Status);
    }
}
