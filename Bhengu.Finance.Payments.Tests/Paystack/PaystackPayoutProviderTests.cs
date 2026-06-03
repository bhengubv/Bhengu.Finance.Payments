// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Internals;
using Bhengu.Finance.Payments.Paystack.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paystack;

public class PaystackPayoutProviderTests
{
    private static PaystackPayoutProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new PaystackOptions { SecretKey = "sk_test_xx" }),
            NullLogger<PaystackPayoutProvider>.Instance,
            new PaystackIdempotencyCache());

    private static PayoutRequest SampleRequest(string? idempotencyKey = null) => new()
    {
        DestinationToken = "RCP_test123",
        Amount = 250m,
        Currency = "NGN",
        Description = "Vendor payout",
        IdempotencyKey = idempotencyKey
    };

    [Fact]
    public void Constructor_Throws_WhenSecretKeyMissing() =>
        Assert.Throws<ProviderConfigurationException>(() => new PaystackPayoutProvider(
            new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new PaystackOptions()),
            NullLogger<PaystackPayoutProvider>.Instance,
            new PaystackIdempotencyCache()));

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("transfer", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"message":"Transfer queued","data":{"transfer_code":"TR_xxx","reference":"transfer-1","status":"success"}}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(SampleRequest());
        Assert.Equal("transfer-1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
    }

    [Fact]
    public async Task ProcessPayoutAsync_StripsRecipientPrefix()
    {
        var capturedBody = string.Empty;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":true,"data":{"reference":"t1","status":"pending"}}""");
        });
        var provider = Create(handler);
        await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "recipient-RCP_real_code",
            Amount = 1m,
            Currency = "NGN",
            Description = "test"
        });
        Assert.Contains("RCP_real_code", capturedBody);
        Assert.DoesNotContain("recipient-RCP_real_code", capturedBody);
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "slow"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPayoutAsync(SampleRequest()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws4xxAsDeclined()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "no balance"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPayoutAsync(SampleRequest()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws5xxAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPayoutAsync(SampleRequest()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_NetworkError_RaisesProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS lookup failed"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPayoutAsync(SampleRequest()));
    }
}
