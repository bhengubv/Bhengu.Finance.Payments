// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.ThreeDSecure;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Bhengu.Finance.Payments.Razorpay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Razorpay;

public class RazorpayThreeDSecureProviderTests
{
    private static RazorpayThreeDSecureProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new RazorpayOptions { KeyId = "rzp_test_x", KeySecret = "secret_x" }),
            NullLogger<RazorpayThreeDSecureProvider>.Instance);

    private static PaymentRequest SampleIntent() => new()
    {
        PaymentMethodToken = "pay_x",
        Amount = 100m,
        Currency = "INR",
        Description = "3DS test",
        CustomerId = "cust_1"
    };

    [Fact]
    public async Task StartAuthenticationAsync_ReturnsChallengeRequired_WithRedirect()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v1/payments/create/json", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"pay_x","status":"created","next":{"action":"redirect","url":"https://acs.example.com/auth"},"authentication":{"ds_transaction_id":"ds_1","version":"2.2.0"}}
                """);
        });
        var provider = Create(handler);
        var result = await provider.StartAuthenticationAsync(SampleIntent());

        Assert.Equal(ThreeDSecureStatus.ChallengeRequired, result.Status);
        Assert.Equal("pay_x", result.ChallengeReference);
        Assert.Equal("https://acs.example.com/auth", result.RedirectUrl);
        Assert.Equal("ds_1", result.DsTransactionId);
        Assert.Equal("2.2.0", result.ProtocolVersion);
    }

    [Fact]
    public async Task StartAuthenticationAsync_AuthorizedFrictionless_ReturnsAuthenticated()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"pay_x","status":"captured"}
                """));
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
            header = req.Headers.TryGetValues("X-Razorpay-IdempotencyKey", out var v) ? string.Join(",", v) : null;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"pay_x","status":"captured"}""");
        });
        var provider = Create(handler);
        await provider.StartAuthenticationAsync(SampleIntent() with { IdempotencyKey = "idem-3ds-1" });
        Assert.Equal("idem-3ds-1", header);
    }

    [Fact]
    public async Task GetChallengeAsync_DeserialisesPaymentStatus()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v1/payments/pay_x", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"pay_x","status":"captured"}""");
        });
        var provider = Create(handler);
        var result = await provider.GetChallengeAsync("pay_x");
        Assert.Equal(ThreeDSecureStatus.Authenticated, result.Status);
    }

    [Fact]
    public async Task GetChallengeAsync_PendingMapped_AsChallengeRequired()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"pay_x","status":"created"}"""));
        var provider = Create(handler);
        var result = await provider.GetChallengeAsync("pay_x");
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
