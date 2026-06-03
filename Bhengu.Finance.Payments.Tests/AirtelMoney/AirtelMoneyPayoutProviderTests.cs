// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.AirtelMoney.Configuration;
using Bhengu.Finance.Payments.AirtelMoney.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.AirtelMoney;

/// <summary>
/// Standalone <see cref="AirtelMoneyPayoutProvider"/> tests. Covers the
/// <c>standard/v1/disbursements/</c> endpoint, OAuth caching, encrypted-PIN passthrough,
/// MSISDN validation and idempotency-id wiring.
/// </summary>
public class AirtelMoneyPayoutProviderTests
{
    private static AirtelMoneyOptions DefaultOptions() => new()
    {
        ClientId = "client-id",
        ClientSecret = "client-secret",
        Country = "KE",
        Currency = "KES",
        CallbackUrl = "https://example.com/airtel/cb",
        WebhookSecret = "airtel-webhook-secret",
        EncryptedDisbursementPin = "rsa-encrypted-pin-base64",
        UseSandbox = true
    };

    private static AirtelMoneyPayoutProvider Create(StubHttpMessageHandler handler, AirtelMoneyOptions? opts = null)
    {
        opts ??= DefaultOptions();
        var http = new HttpClient(handler);
        return new AirtelMoneyPayoutProvider(http, Options.Create(opts), NullLogger<AirtelMoneyPayoutProvider>.Instance);
    }

    private static StubHttpMessageHandler OAuthAware(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> operationHandler,
        Action<HttpRequestMessage>? onOAuthHit = null) =>
        new((req, ct) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("auth/oauth2/token", StringComparison.Ordinal))
            {
                onOAuthHit?.Invoke(req);
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"access_token":"airtel-disb-test-token","token_type":"Bearer","expires_in":3599}
                    """);
            }
            return operationHandler(req, ct);
        });

    private static PayoutRequest SamplePayout(string? idempotencyKey = null) => new()
    {
        DestinationToken = "254712345678",
        Amount = 500m,
        Currency = "KES",
        Description = "Driver payout",
        IdempotencyKey = idempotencyKey
    };

    [Fact]
    public void Constructor_Throws_WhenClientIdMissing()
    {
        var opts = DefaultOptions();
        opts.ClientId = string.Empty;
        Assert.Throws<ProviderConfigurationException>(() =>
            Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), opts));
    }

    [Fact]
    public void Constructor_Throws_WhenCurrencyMissing()
    {
        var opts = DefaultOptions();
        opts.Currency = string.Empty;
        Assert.Throws<ProviderConfigurationException>(() =>
            Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), opts));
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsCompletedResponse_OnSuccess()
    {
        var handler = OAuthAware((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("standard/v1/disbursements", req.RequestUri!.PathAndQuery);
            Assert.True(req.Headers.Contains("X-Country"));
            Assert.True(req.Headers.Contains("X-Currency"));
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":{"transaction":{"id":"DISB-1","status":"TS"}},"status":{"success":true}}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(SamplePayout());

        Assert.Equal("DISB-1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
        Assert.Equal("KES", payout.Currency);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ThrowsForBlankDestinationToken()
    {
        var provider = Create(OAuthAware((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            provider.ProcessPayoutAsync(new PayoutRequest
            {
                DestinationToken = string.Empty,
                Amount = 100m,
                Currency = "KES",
                Description = "x"
            }));
        Assert.Equal("invalid_msisdn", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws429AsRateLimit()
    {
        var handler = OAuthAware((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "limit"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPayoutAsync(SamplePayout()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws4xxAsPaymentDeclined()
    {
        var handler = OAuthAware((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "DP00800001006"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPayoutAsync(SamplePayout()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws5xxAsProviderUnavailable()
    {
        var handler = OAuthAware((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPayoutAsync(SamplePayout()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_WrapsHttpRequestExceptionAsProviderUnavailable()
    {
        var handler = OAuthAware((_, _) => throw new HttpRequestException("DNS fail"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPayoutAsync(SamplePayout()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_ForwardsIdempotencyKey_AsTransactionIdAndReference()
    {
        var captured = string.Empty;
        var handler = OAuthAware((req, _) =>
        {
            captured = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":{"transaction":{"id":"caller-key-99","status":"TIP"}},"status":{"success":true}}
                """);
        });
        var provider = Create(handler);
        await provider.ProcessPayoutAsync(SamplePayout(idempotencyKey: "caller-key-99"));

        Assert.Contains("\"id\":\"caller-key-99\"", captured);
        Assert.Contains("\"reference\":\"caller-key-99\"", captured);
    }

    [Fact]
    public async Task ProcessPayoutAsync_IncludesEncryptedPin_FromOptions()
    {
        var captured = string.Empty;
        var handler = OAuthAware((req, _) =>
        {
            captured = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":{"transaction":{"id":"x","status":"TS"}},"status":{"success":true}}
                """);
        });
        var provider = Create(handler);
        await provider.ProcessPayoutAsync(SamplePayout());

        Assert.Contains("\"pin\":\"rsa-encrypted-pin-base64\"", captured);
    }

    [Fact]
    public async Task ProcessPayoutAsync_CachesOAuthTokenAcrossMultipleCalls()
    {
        var oauthHits = 0;
        var handler = OAuthAware(
            (_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":{"transaction":{"id":"x","status":"TS"}},"status":{"success":true}}
                """),
            onOAuthHit: _ => Interlocked.Increment(ref oauthHits));
        var provider = Create(handler);

        await provider.ProcessPayoutAsync(SamplePayout());
        await provider.ProcessPayoutAsync(SamplePayout());
        await provider.ProcessPayoutAsync(SamplePayout());

        Assert.Equal(1, oauthHits);
    }
}
