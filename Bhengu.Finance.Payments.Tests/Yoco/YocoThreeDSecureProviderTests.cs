// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.ThreeDSecure;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.Yoco.Configuration;
using Bhengu.Finance.Payments.Yoco.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Yoco;

public class YocoThreeDSecureProviderTests
{
    private static YocoThreeDSecureProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new YocoOptions { SecretKey = "sk_test_x" }),
            NullLogger<YocoThreeDSecureProvider>.Instance);

    private static PaymentRequest SampleIntent() => new()
    {
        PaymentMethodToken = "tok_visa",
        Amount = 100m,
        Currency = "ZAR",
        Description = "3DS test"
    };

    [Fact]
    public async Task StartAuthenticationAsync_ReturnsChallengeRequired_WithRedirect()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("charges/", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"ch_1","status":"pending","nextAction":{"type":"redirect","redirectUrl":"https://acs.example.com/ch_1"},"threeDSecure":{"version":"2.2.0","dsTransactionId":"ds_1","eci":"05"}}
                """);
        });
        var provider = Create(handler);
        var result = await provider.StartAuthenticationAsync(SampleIntent());

        Assert.Equal(ThreeDSecureStatus.ChallengeRequired, result.Status);
        Assert.Equal("ch_1", result.ChallengeReference);
        Assert.Equal("https://acs.example.com/ch_1", result.RedirectUrl);
        Assert.Equal("ds_1", result.DsTransactionId);
    }

    [Fact]
    public async Task StartAuthenticationAsync_Frictionless_ReturnsAuthenticated()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"ch_2","status":"successful"}"""));
        var provider = Create(handler);
        var result = await provider.StartAuthenticationAsync(SampleIntent());
        Assert.Equal(ThreeDSecureStatus.Authenticated, result.Status);
    }

    [Fact]
    public async Task StartAuthenticationAsync_SendsIdempotencyKey()
    {
        string? header = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            header = req.Headers.TryGetValues("Idempotency-Key", out var v) ? string.Join(",", v) : null;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"ch_3","status":"successful"}""");
        });
        var provider = Create(handler);
        await provider.StartAuthenticationAsync(SampleIntent() with { IdempotencyKey = "idem-3ds-1" });
        Assert.Equal("idem-3ds-1", header);
    }

    [Fact]
    public async Task GetChallengeAsync_ReturnsAuthenticated_OnSuccessful()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("charges/ch_1", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"ch_1","status":"successful"}""");
        });
        var provider = Create(handler);
        var result = await provider.GetChallengeAsync("ch_1");
        Assert.Equal(ThreeDSecureStatus.Authenticated, result.Status);
    }

    [Fact]
    public async Task GetChallengeAsync_PendingMapped_AsChallengeRequired()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"ch_1","status":"pending"}"""));
        var provider = Create(handler);
        var result = await provider.GetChallengeAsync("ch_1");
        Assert.Equal(ThreeDSecureStatus.ChallengeRequired, result.Status);
    }

    [Fact]
    public async Task StartAuthenticationAsync_OnNetworkFailure_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.StartAuthenticationAsync(SampleIntent()));
    }
}
