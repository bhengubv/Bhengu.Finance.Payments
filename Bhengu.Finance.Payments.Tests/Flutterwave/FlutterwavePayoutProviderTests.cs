// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Bhengu.Finance.Payments.Flutterwave.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Flutterwave;

/// <summary>
/// Payout is implemented on <see cref="FlutterwavePaymentProvider"/> (which also implements
/// <c>IPaymentGatewayProvider</c>). These tests target the IPayoutProvider contract on the same
/// instance with extra coverage for destination-token parsing, exception translation and
/// idempotency wiring.
/// </summary>
public class FlutterwavePayoutProviderTests
{
    private static FlutterwavePaymentProvider Create(StubHttpMessageHandler handler, FlutterwaveOptions? opts = null)
    {
        opts ??= new FlutterwaveOptions { SecretKey = "FLWSECK_TEST-xxx" };
        var http = new HttpClient(handler);
        return new FlutterwavePaymentProvider(http, Options.Create(opts), NullLogger<FlutterwavePaymentProvider>.Instance);
    }

    private static PayoutRequest SamplePayout(string? idempotencyKey = null) => new()
    {
        DestinationToken = "044:0690000040",
        Amount = 500m,
        Currency = "NGN",
        Description = "Vendor payout",
        IdempotencyKey = idempotencyKey
    };

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v3/transfers", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","message":"Transfer Queued","data":{"id":1,"reference":"transfer-1","status":"NEW","amount":500}}
                """);
        });
        var provider = Create(handler);

        var response = await provider.ProcessPayoutAsync(SamplePayout());
        Assert.Equal("transfer-1", response.GatewayReference);
        Assert.Equal(500m, response.Amount);
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws_WhenDestinationMalformed()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "no-colon",
            Amount = 100m,
            Currency = "NGN",
            Description = "x"
        }));
        Assert.Equal("invalid_destination", ex.ProviderErrorCode);
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
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "invalid bank code"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPayoutAsync(SamplePayout()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws5xxAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.InternalServerError, "down"));
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
    public async Task ProcessPayoutAsync_PostsBankCodeAndAccountNumber()
    {
        var captured = string.Empty;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            captured = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","data":{"id":1,"reference":"transfer-1","status":"NEW","amount":500}}
                """);
        });
        var provider = Create(handler);
        await provider.ProcessPayoutAsync(SamplePayout());
        Assert.Contains("\"account_bank\":\"044\"", captured);
        Assert.Contains("\"account_number\":\"0690000040\"", captured);
    }
}
