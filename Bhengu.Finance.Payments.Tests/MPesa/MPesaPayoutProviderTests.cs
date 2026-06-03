// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.MPesa.Configuration;
using Bhengu.Finance.Payments.MPesa.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.MPesa;

/// <summary>
/// Standalone <see cref="MPesaPayoutProvider"/> tests. Mirrors the IPaymentGatewayProvider's
/// own B2C coverage but exercises the dedicated payout type — including OAuth token caching,
/// MSISDN-format normalisation, and OriginatorConversationID idempotency passthrough.
/// </summary>
public class MPesaPayoutProviderTests
{
    private static MPesaOptions DefaultOptions() => new()
    {
        ConsumerKey = "ck_test",
        ConsumerSecret = "cs_test",
        BusinessShortCode = "600999",
        Passkey = "bfb279f9aa9bdbcf158e97dd71a467cd2e0c893059b10f78e6b72ada1ed2c919",
        InitiatorName = "testapi",
        SecurityCredential = "Safaricom999!*!",
        QueueTimeoutUrl = "https://example.com/mpesa/timeout",
        ResultUrl = "https://example.com/mpesa/result",
        UseSandbox = true
    };

    private static MPesaPayoutProvider Create(StubHttpMessageHandler handler, MPesaOptions? opts = null)
    {
        opts ??= DefaultOptions();
        var http = new HttpClient(handler);
        return new MPesaPayoutProvider(http, Options.Create(opts), NullLogger<MPesaPayoutProvider>.Instance);
    }

    // Token endpoint returns a fixed token so we can assert OAuth caching by counting hits.
    private static StubHttpMessageHandler OAuthAware(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> operationHandler,
        Action<HttpRequestMessage>? onOAuthHit = null) =>
        new((req, ct) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("oauth/v1/generate", StringComparison.Ordinal))
            {
                onOAuthHit?.Invoke(req);
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"access_token":"mpesa-test-token","expires_in":"3599"}
                    """);
            }
            return operationHandler(req, ct);
        });

    private static PayoutRequest SamplePayout(string? idempotencyKey = null) => new()
    {
        DestinationToken = "254712345678",
        Amount = 1500m,
        Currency = "KES",
        Description = "Vendor payout",
        IdempotencyKey = idempotencyKey
    };

    [Fact]
    public void Constructor_Throws_WhenInitiatorNameMissing()
    {
        var opts = DefaultOptions();
        opts.InitiatorName = string.Empty;
        var ex = Assert.Throws<ProviderConfigurationException>(() =>
            Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), opts));
        Assert.Contains("InitiatorName", ex.Message);
    }

    [Fact]
    public void Constructor_Throws_WhenSecurityCredentialMissing()
    {
        var opts = DefaultOptions();
        opts.SecurityCredential = string.Empty;
        Assert.Throws<ProviderConfigurationException>(() =>
            Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), opts));
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsPendingResponse_OnAcceptedB2C()
    {
        var handler = OAuthAware((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("mpesa/b2c/v3/paymentrequest", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"OriginatorConversationID":"5118-111210482-1","ConversationID":"AG_20191219_00005797af5d7d75f652","ResponseCode":"0","ResponseDescription":"Accept the service request successfully."}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(SamplePayout());

        Assert.Equal("AG_20191219_00005797af5d7d75f652", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, payout.Status);
        Assert.Equal(1500m, payout.Amount);
        Assert.Equal("KES", payout.Currency);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsFailedResponse_OnNonZeroResponseCode()
    {
        var handler = OAuthAware((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"OriginatorConversationID":"x","ConversationID":"AG_x","ResponseCode":"1","ResponseDescription":"Insufficient float"}
            """));
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(SamplePayout());
        Assert.Equal(PaymentStatus.Failed, payout.Status);
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
        var handler = OAuthAware((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate-limited"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPayoutAsync(SamplePayout()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws4xxAsPaymentDeclined()
    {
        var handler = OAuthAware((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "Invalid receiver party"));
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
    public async Task ProcessPayoutAsync_PassesIdempotencyKey_AsOriginatorConversationId()
    {
        var captured = string.Empty;
        var handler = OAuthAware((req, _) =>
        {
            captured = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"OriginatorConversationID":"caller-key-123","ConversationID":"AG_x","ResponseCode":"0"}
                """);
        });
        var provider = Create(handler);
        await provider.ProcessPayoutAsync(SamplePayout(idempotencyKey: "caller-key-123"));

        Assert.Contains("\"OriginatorConversationID\":\"caller-key-123\"", captured);
        Assert.Contains("\"SecurityCredential\":\"Safaricom999!*!\"", captured);
    }

    [Fact]
    public async Task ProcessPayoutAsync_DefaultsToBusinessPayment_OrHonoursMetadataCommandId()
    {
        var captured = string.Empty;
        var handler = OAuthAware((req, _) =>
        {
            captured = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"OriginatorConversationID":"x","ConversationID":"AG_x","ResponseCode":"0"}
                """);
        });
        var provider = Create(handler);
        await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "254712345678",
            Amount = 200m,
            Currency = "KES",
            Description = "Salary",
            Metadata = new Dictionary<string, string> { ["command_id"] = "SalaryPayment" }
        });

        Assert.Contains("\"CommandID\":\"SalaryPayment\"", captured);
    }

    [Fact]
    public async Task ProcessPayoutAsync_CachesOAuthTokenAcrossMultipleCalls()
    {
        var oauthHits = 0;
        var handler = OAuthAware(
            (_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"OriginatorConversationID":"x","ConversationID":"AG_x","ResponseCode":"0"}
                """),
            onOAuthHit: _ => Interlocked.Increment(ref oauthHits));
        var provider = Create(handler);

        await provider.ProcessPayoutAsync(SamplePayout("k1"));
        await provider.ProcessPayoutAsync(SamplePayout("k2"));
        await provider.ProcessPayoutAsync(SamplePayout("k3"));

        Assert.Equal(1, oauthHits);
    }
}
