// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Kashier.Configuration;
using Bhengu.Finance.Payments.Kashier.Internals;
using Bhengu.Finance.Payments.Kashier.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Kashier;

public class KashierTokenisationProviderTests
{
    private static KashierTokenisationProvider Create(StubHttpMessageHandler handler, KashierOptions? opts = null)
    {
        opts ??= new KashierOptions { ApiKey = "k", MerchantId = "MID", SecretKey = "s", Currency = "EGP" };
        var http = new HttpClient(handler);
        return new KashierTokenisationProvider(http, Options.Create(opts), NullLogger<KashierTokenisationProvider>.Instance,
            new KashierIdempotencyCache(new InMemoryBhenguDistributedCache()));
    }

    private static TokeniseRequest SampleRequest(string? idem = null) => new()
    {
        Card = new CardDetails
        {
            CardholderName = "Buyer",
            CardNumber = "4111111111111111",
            ExpiryMonth = 11,
            ExpiryYear = 2031,
            Cvv = "100"
        },
        CustomerId = "shop-1",
        DisplayName = "Personal card",
        SetAsDefault = true,
        IdempotencyKey = idem
    };

    [Fact]
    public async Task TokeniseAsync_ReturnsPaymentMethod_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"status":"SUCCESS","response":{"cardToken":"CT_x","shopperReference":"shop-1","brand":"visa","last4":"1111","expiryMonth":"11","expiryYear":"2031"}}"""));
        var provider = Create(handler);
        var pm = await provider.TokeniseAsync(SampleRequest());
        Assert.Equal("CT_x", pm.Token);
        Assert.Equal("shop-1", pm.CustomerId);
        Assert.Equal(PaymentMethodKind.Card, pm.Kind);
        Assert.Equal("visa", pm.Brand);
        Assert.Equal("1111", pm.Last4);
    }

    [Fact]
    public async Task TokeniseAsync_ThrowsPaymentDeclined_WhenNoCardToken()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"status":"SUCCESS","response":{"brand":"visa"}}"""));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_WrapsNetworkAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_Dedupes_OnSameIdempotencyKey()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                "{\"status\":\"SUCCESS\",\"response\":{\"cardToken\":\"CT_" + calls + "\",\"brand\":\"visa\"}}");
        });
        var provider = Create(handler);
        var r1 = await provider.TokeniseAsync(SampleRequest("idem-1"));
        var r2 = await provider.TokeniseAsync(SampleRequest("idem-1"));
        Assert.Equal(r1.Token, r2.Token);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ListPaymentMethodsAsync_MapsCollection()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"status":"SUCCESS","response":[{"cardToken":"A","brand":"visa","last4":"1111"},{"cardToken":"B","brand":"mc","last4":"4242"}]}"""));
        var provider = Create(handler);
        var list = await provider.ListPaymentMethodsAsync("shop-1");
        Assert.Equal(2, list.Count);
        Assert.Equal("4242", list[1].Last4);
    }

    [Fact]
    public async Task DeletePaymentMethodAsync_ReturnsTrue_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Delete, req.Method);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        });
        var provider = Create(handler);
        Assert.True(await provider.DeletePaymentMethodAsync("CT_x"));
    }
}
