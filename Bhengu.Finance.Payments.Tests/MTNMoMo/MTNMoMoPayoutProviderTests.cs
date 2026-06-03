// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.MTNMoMo.Configuration;
using Bhengu.Finance.Payments.MTNMoMo.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.MTNMoMo;

/// <summary>
/// Standalone <see cref="MTNMoMoPayoutProvider"/> tests. Covers OAuth caching across multiple
/// transfers, X-Reference-Id idempotency passthrough, and the MoMo-specific 202 Accepted +
/// empty-body settlement model.
/// </summary>
public class MTNMoMoPayoutProviderTests
{
    private static MTNMoMoOptions DefaultOptions() => new()
    {
        SubscriptionKey = "disb-sub-key",
        ApiUserId = "00000000-0000-0000-0000-000000000002",
        ApiKey = "disb-api-key",
        TargetEnvironment = "sandbox",
        CallbackUrl = "https://example.com/momo/disb/cb",
        UseSandbox = true
    };

    private static MTNMoMoPayoutProvider Create(StubHttpMessageHandler handler, MTNMoMoOptions? opts = null)
    {
        opts ??= DefaultOptions();
        var http = new HttpClient(handler);
        return new MTNMoMoPayoutProvider(http, Options.Create(opts), NullLogger<MTNMoMoPayoutProvider>.Instance);
    }

    private static StubHttpMessageHandler OAuthAware(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> operationHandler,
        Action<HttpRequestMessage>? onOAuthHit = null) =>
        new((req, ct) =>
        {
            if (req.RequestUri!.PathAndQuery.EndsWith("/token/", StringComparison.Ordinal))
            {
                onOAuthHit?.Invoke(req);
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"access_token":"momo-disb-test-token","token_type":"Bearer","expires_in":3599}
                    """);
            }
            return operationHandler(req, ct);
        });

    private static PayoutRequest SamplePayout(string? idempotencyKey = null) => new()
    {
        DestinationToken = "256776123456",
        Amount = 750m,
        Currency = "EUR",
        Description = "Driver payout",
        IdempotencyKey = idempotencyKey
    };

    [Fact]
    public void Constructor_Throws_WhenSubscriptionKeyMissing()
    {
        var opts = DefaultOptions();
        opts.SubscriptionKey = string.Empty;
        Assert.Throws<ProviderConfigurationException>(() =>
            Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), opts));
    }

    [Fact]
    public void Constructor_Throws_WhenTargetEnvironmentMissing()
    {
        var opts = DefaultOptions();
        opts.TargetEnvironment = string.Empty;
        Assert.Throws<ProviderConfigurationException>(() =>
            Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), opts));
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsPendingResponse_OnAcceptedTransfer()
    {
        var handler = OAuthAware((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("disbursement/v1_0/transfer", req.RequestUri!.PathAndQuery);
            Assert.True(req.Headers.Contains("X-Reference-Id"));
            Assert.True(req.Headers.Contains("X-Target-Environment"));
            Assert.True(req.Headers.Contains("Ocp-Apim-Subscription-Key"));
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(SamplePayout());

        Assert.True(Guid.TryParse(payout.GatewayReference, out _), "GatewayReference should be a UUID");
        Assert.Equal(PaymentStatus.Pending, payout.Status);
        Assert.Equal(750m, payout.Amount);
        Assert.Equal("EUR", payout.Currency);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ThrowsForBlankDestinationToken()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.Accepted)));
        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            provider.ProcessPayoutAsync(new PayoutRequest
            {
                DestinationToken = string.Empty,
                Amount = 1m,
                Currency = "EUR",
                Description = "x"
            }));
        Assert.Equal("invalid_msisdn", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws429AsRateLimit()
    {
        var handler = OAuthAware((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "throttled"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPayoutAsync(SamplePayout()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws4xxAsPaymentDeclined()
    {
        var handler = OAuthAware((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "PAYEE_NOT_FOUND"));
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
    public async Task ProcessPayoutAsync_ForwardsValidGuidIdempotencyKey_AsXReferenceId()
    {
        var capturedRef = string.Empty;
        var capturedBody = string.Empty;
        var idempotency = Guid.NewGuid().ToString();
        var handler = OAuthAware((req, _) =>
        {
            if (req.Headers.TryGetValues("X-Reference-Id", out var values))
                capturedRef = values.FirstOrDefault() ?? string.Empty;
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(SamplePayout(idempotency));

        Assert.Equal(idempotency, capturedRef);
        Assert.Equal(idempotency, payout.GatewayReference);
        Assert.Contains($"\"externalId\":\"{idempotency}\"", capturedBody);
    }

    [Fact]
    public async Task ProcessPayoutAsync_GeneratesUuid_ForNonGuidIdempotencyKey()
    {
        var capturedRef = string.Empty;
        var handler = OAuthAware((req, _) =>
        {
            if (req.Headers.TryGetValues("X-Reference-Id", out var values))
                capturedRef = values.FirstOrDefault() ?? string.Empty;
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        var provider = Create(handler);
        await provider.ProcessPayoutAsync(SamplePayout("not-a-uuid-business-id"));

        // Header must always be a valid UUID even when the caller's key is not.
        Assert.True(Guid.TryParse(capturedRef, out _));
    }

    [Fact]
    public async Task ProcessPayoutAsync_CachesOAuthTokenAcrossMultipleCalls()
    {
        var oauthHits = 0;
        var handler = OAuthAware(
            (_, _) => new HttpResponseMessage(HttpStatusCode.Accepted),
            onOAuthHit: _ => Interlocked.Increment(ref oauthHits));
        var provider = Create(handler);

        await provider.ProcessPayoutAsync(SamplePayout());
        await provider.ProcessPayoutAsync(SamplePayout());
        await provider.ProcessPayoutAsync(SamplePayout());

        Assert.Equal(1, oauthHits);
    }
}
