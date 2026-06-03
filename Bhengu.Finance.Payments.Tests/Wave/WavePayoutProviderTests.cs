// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.Wave.Configuration;
using Bhengu.Finance.Payments.Wave.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Wave;

/// <summary>
/// Standalone <see cref="WavePayoutProvider"/> tests. Covers the <c>v1/payout</c> endpoint,
/// country-code parsing for <c>&lt;country&gt;:&lt;phone&gt;</c> destinations and Wave's native
/// idempotency-key body field.
/// </summary>
public class WavePayoutProviderTests
{
    private static WavePayoutProvider Create(StubHttpMessageHandler handler, WaveOptions? opts = null)
    {
        opts ??= new WaveOptions
        {
            ApiKey = "wave_sn_prod_xxx",
            WebhookSecret = "webhook-test-secret",
            Currency = "XOF"
        };
        var http = new HttpClient(handler);
        return new WavePayoutProvider(http, Options.Create(opts), NullLogger<WavePayoutProvider>.Instance);
    }

    private static PayoutRequest SamplePayout(string? idempotencyKey = null) => new()
    {
        DestinationToken = "SN:221761234567",
        Amount = 2500m,
        Currency = "XOF",
        Description = "Vendor payout",
        IdempotencyKey = idempotencyKey
    };

    [Fact]
    public void Constructor_Throws_WhenApiKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new WavePayoutProvider(http, Options.Create(new WaveOptions()), NullLogger<WavePayoutProvider>.Instance));
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("v1/payout", req.RequestUri!.PathAndQuery);
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"po-wave-1","status":"processing","receive_amount":"2500","currency":"XOF"}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(SamplePayout());

        Assert.Equal("po-wave-1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, payout.Status);
        Assert.Equal(2500m, payout.Amount);
        Assert.Equal("XOF", payout.Currency);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ThrowsForBlankDestinationToken()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            provider.ProcessPayoutAsync(new PayoutRequest
            {
                DestinationToken = string.Empty,
                Amount = 100m,
                Currency = "XOF",
                Description = "x"
            }));
        Assert.Equal("invalid_msisdn", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate-limited"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPayoutAsync(SamplePayout()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws4xxAsPaymentDeclined()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "recipient-not-found"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPayoutAsync(SamplePayout()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws5xxAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPayoutAsync(SamplePayout()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_WrapsHttpRequestExceptionAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS fail"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPayoutAsync(SamplePayout()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_PassesIdempotencyKey_InBody()
    {
        var captured = string.Empty;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            captured = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"po-wave-1","status":"processing","currency":"XOF"}
                """);
        });
        var provider = Create(handler);
        await provider.ProcessPayoutAsync(SamplePayout(idempotencyKey: "caller-payout-key-42"));

        Assert.Contains("\"idempotency_key\":\"caller-payout-key-42\"", captured);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ParsesCountryAndPhone_FromColonSeparatedToken()
    {
        var captured = string.Empty;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            captured = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"po-wave-1","status":"processing","currency":"XOF"}
                """);
        });
        var provider = Create(handler);
        await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "CI:0701234567",
            Amount = 1000m,
            Currency = "XOF",
            Description = "Vendor payout"
        });

        Assert.Contains("\"country_code\":\"CI\"", captured);
        Assert.Contains("\"national_id\":\"0701234567\"", captured);
    }

    [Fact]
    public async Task ProcessPayoutAsync_DefaultsToSenegal_ForRawPhone()
    {
        var captured = string.Empty;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            captured = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"po-wave-1","status":"processing","currency":"XOF"}
                """);
        });
        var provider = Create(handler);
        await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "221761234567",
            Amount = 1000m,
            Currency = "XOF",
            Description = "Default-country payout"
        });

        Assert.Contains("\"country_code\":\"SN\"", captured);
        Assert.Contains("\"national_id\":\"221761234567\"", captured);
    }
}
