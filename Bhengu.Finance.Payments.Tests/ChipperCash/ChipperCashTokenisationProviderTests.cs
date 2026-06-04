// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.ChipperCash.Configuration;
using Bhengu.Finance.Payments.ChipperCash.Providers;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.ChipperCash;

public class ChipperCashTokenisationProviderTests
{
    private static ChipperCashOptions DefaultOptions() => new()
    {
        ApiKey = "chp-api-key",
        ApiSecret = "chp-api-secret",
        MerchantId = "MERCH-CHP",
        Country = "NG",
        Currency = "NGN"
    };

    private static ChipperCashTokenisationProvider CreateRead(StubHttpMessageHandler handler, ChipperCashOptions? opts = null)
    {
        opts ??= DefaultOptions();
        var http = new HttpClient(handler);
        return new ChipperCashTokenisationProvider(http, Options.Create(opts), NullLogger<ChipperCashTokenisationProvider>.Instance);
    }

    private static ChipperCashRawCardTokenisationProvider CreateRaw(StubHttpMessageHandler handler, IBhenguDistributedCache? cache = null, ChipperCashOptions? opts = null)
    {
        opts ??= DefaultOptions();
        var http = new HttpClient(handler);
        return new ChipperCashRawCardTokenisationProvider(http, Options.Create(opts), NullLogger<ChipperCashRawCardTokenisationProvider>.Instance, cache);
    }

    private static TokeniseRequest SampleRequest(string? key = null) => new()
    {
        Card = new CardDetails
        {
            CardholderName = "MTN",
            CardNumber = "+2348012345678",
            ExpiryMonth = 12,
            ExpiryYear = 2030
        },
        CustomerId = "CUST-1",
        DisplayName = "My MTN line",
        IdempotencyKey = key
    };

    [Fact]
    public void Constructor_Throws_WhenApiKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new ChipperCashTokenisationProvider(http,
                Options.Create(new ChipperCashOptions { ApiSecret = "s" }),
                NullLogger<ChipperCashTokenisationProvider>.Instance));
    }

    [Fact]
    public async Task TokeniseAsync_ReturnsPaymentMethod_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("v1/recipients", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"REC-1","customerId":"CUST-1","name":"My MTN line","msisdn":"+2348012345678","network":"MTN","isDefault":false}
                """);
        });
        var provider = CreateRaw(handler);
        var method = await provider.TokeniseAsync(SampleRequest());

        Assert.Equal("REC-1", method.Token);
        Assert.Equal("CUST-1", method.CustomerId);
        Assert.Equal(PaymentMethodKind.MobileMoney, method.Kind);
        Assert.Equal("MTN", method.Brand);
    }

    [Fact]
    public async Task TokeniseAsync_DedupesViaIdempotencyKey()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"REC-1","customerId":"CUST-1","name":"X","msisdn":"+2348012345678","network":"MTN"}
                """);
        });
        var cache = new InMemoryBhenguDistributedCache();
        var provider = CreateRaw(handler, cache);
        var first = await provider.TokeniseAsync(SampleRequest("idem-1"));
        var second = await provider.TokeniseAsync(SampleRequest("idem-1"));

        Assert.Equal(first.Token, second.Token);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TokeniseAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate limited"));
        var provider = CreateRaw(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad"));
        var provider = CreateRaw(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS fail"));
        var provider = CreateRaw(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task GetPaymentMethodAsync_ReturnsNull_On404()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = CreateRead(handler);
        Assert.Null(await provider.GetPaymentMethodAsync("missing"));
    }

    [Fact]
    public async Task DeletePaymentMethodAsync_ReturnsTrue_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Delete, req.Method);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        });
        var provider = CreateRead(handler);
        Assert.True(await provider.DeletePaymentMethodAsync("REC-1"));
    }
}
