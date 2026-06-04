// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Paymob.Configuration;
using Bhengu.Finance.Payments.Paymob.Internals;
using Bhengu.Finance.Payments.Paymob.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paymob;

public class PaymobTokenisationProviderTests
{
    private static PaymobTokenisationProvider Create(StubHttpMessageHandler handler, PaymobOptions? opts = null)
    {
        opts ??= new PaymobOptions { ApiKey = "api_test_key", IntegrationId = 100 };
        var http = new HttpClient(handler);
        return new PaymobTokenisationProvider(http, Options.Create(opts), NullLogger<PaymobTokenisationProvider>.Instance,
            new PaymobIdempotencyCache(new InMemoryBhenguDistributedCache()));
    }

    private static TokeniseRequest SampleRequest(string? idempotencyKey = null) => new()
    {
        Card = new CardDetails
        {
            CardholderName = "Test Buyer",
            CardNumber = "4111111111111111",
            ExpiryMonth = 12,
            ExpiryYear = 2030,
            Cvv = "123"
        },
        CustomerId = "cust-1",
        DisplayName = "Personal card",
        SetAsDefault = true,
        IdempotencyKey = idempotencyKey
    };

    [Fact]
    public void Constructor_Throws_WhenApiKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() => new PaymobTokenisationProvider(
            http, Options.Create(new PaymobOptions()), NullLogger<PaymobTokenisationProvider>.Instance,
            new PaymobIdempotencyCache(new InMemoryBhenguDistributedCache())));
    }

    [Fact]
    public async Task TokeniseAsync_ReturnsPaymentMethod_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var p = req.RequestUri!.PathAndQuery;
            if (p.Contains("auth/tokens", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"tok"}""");
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"card_token":"CT_abc","masked_pan":"411111******1111","card_subtype":"VISA"}""");
        });
        var provider = Create(handler);
        var pm = await provider.TokeniseAsync(SampleRequest());
        Assert.Equal("CT_abc", pm.Token);
        Assert.Equal("cust-1", pm.CustomerId);
        Assert.Equal(PaymentMethodKind.Card, pm.Kind);
        Assert.Equal("VISA", pm.Brand);
        Assert.Equal("1111", pm.Last4);
        Assert.True(pm.IsDefault);
    }

    [Fact]
    public async Task TokeniseAsync_ThrowsPaymentDeclined_WhenNoCardToken()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("auth/tokens", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"tok"}""");
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"masked_pan":"411111******1111"}""");
        });
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "limit"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_Throws5xxAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.InternalServerError, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_WrapsNetworkAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_DedupesOnSameIdempotencyKey()
    {
        var charges = 0;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("auth/tokens", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"tok"}""");
            charges++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, $$"""{"card_token":"CT_{{charges}}","card_subtype":"VISA"}""");
        });
        var provider = Create(handler);
        var r1 = await provider.TokeniseAsync(SampleRequest("idem-1"));
        var r2 = await provider.TokeniseAsync(SampleRequest("idem-1"));
        Assert.Equal(r1.Token, r2.Token);
        Assert.Equal(1, charges);
    }

    [Fact]
    public async Task DeletePaymentMethodAsync_ReturnsTrue_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("auth/tokens", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"token":"tok"}""");
            Assert.Equal(HttpMethod.Delete, req.Method);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        });
        var provider = Create(handler);
        Assert.True(await provider.DeletePaymentMethodAsync("CT_x"));
    }
}
