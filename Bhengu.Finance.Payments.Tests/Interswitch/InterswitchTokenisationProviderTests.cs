// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Interswitch.Configuration;
using Bhengu.Finance.Payments.Interswitch.Internals;
using Bhengu.Finance.Payments.Interswitch.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Interswitch;

public class InterswitchTokenisationProviderTests
{
    private const string TokenJson = """{"access_token":"isw-tok","token_type":"bearer","expires_in":3600}""";

    private static InterswitchOptions DefaultOptions() => new()
    {
        ClientId = "isw-id",
        ClientSecret = "isw-secret",
        MerchantCode = "MX1",
        ProductId = "1",
        WebhookSecret = "wh"
    };

    private static InterswitchTokenisationProvider CreateRead(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), Options.Create(DefaultOptions()),
            NullLogger<InterswitchTokenisationProvider>.Instance);

    private static InterswitchRawCardTokenisationProvider CreateRaw(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        var cache = new InterswitchIdempotencyCache(new InMemoryBhenguDistributedCache());
        return new InterswitchRawCardTokenisationProvider(http, Options.Create(DefaultOptions()),
            NullLogger<InterswitchRawCardTokenisationProvider>.Instance, cache);
    }

    private static StubHttpMessageHandler TokenThen(Func<HttpRequestMessage, HttpResponseMessage> apiHandler) =>
        new((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth/token", StringComparison.OrdinalIgnoreCase))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, TokenJson);
            return apiHandler(req);
        });

    private static TokeniseRequest SampleRequest(string? idempotencyKey = null) => new()
    {
        Card = new CardDetails
        {
            CardholderName = "Test Buyer",
            CardNumber = "5078500000000000",
            ExpiryMonth = 6,
            ExpiryYear = 2030,
            Cvv = "123"
        },
        CustomerId = "CUS-001",
        DisplayName = "Default",
        SetAsDefault = true,
        IdempotencyKey = idempotencyKey
    };

    [Fact]
    public async Task TokeniseAsync_ReturnsPaymentMethod_OnSuccess()
    {
        var handler = TokenThen(req =>
        {
            Assert.Contains("payment/v2/save-card", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"cardToken":"isw-card-xyz","customerId":"CUS-001","maskedPan":"507850******0000",
                 "expiryDate":"0630","cardScheme":"verve","alias":"Default","defaultCard":true}
                """);
        });
        var provider = CreateRaw(handler);
        var pm = await provider.TokeniseAsync(SampleRequest());

        Assert.Equal("isw-card-xyz", pm.Token);
        Assert.Equal("CUS-001", pm.CustomerId);
        Assert.Equal(PaymentMethodKind.Card, pm.Kind);
        Assert.Equal("verve", pm.Brand);
        Assert.Equal("0000", pm.Last4);
        Assert.Equal(6, pm.ExpiryMonth);
        Assert.Equal(2030, pm.ExpiryYear);
        Assert.True(pm.IsDefault);
    }

    [Fact]
    public async Task TokeniseAsync_Throws_OnPaymentDeclined_WhenNoToken()
    {
        var handler = TokenThen(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"responseCode":"05","responseDescription":"do not honor"}"""));
        var provider = CreateRaw(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_Throws429AsProviderRateLimit()
    {
        var handler = TokenThen(_ => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = CreateRaw(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_DedupesOnIdempotencyKey()
    {
        var calls = 0;
        var handler = TokenThen(_ =>
        {
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"cardToken":"isw-card-dup","customerId":"CUS-001","maskedPan":"507850******0000","expiryDate":"0630","cardScheme":"verve"}
                """);
        });
        var provider = CreateRaw(handler);
        var key = $"idemp-{Guid.NewGuid():N}";
        var first = await provider.TokeniseAsync(SampleRequest(key));
        var second = await provider.TokeniseAsync(SampleRequest(key));
        Assert.Equal(first.Token, second.Token);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TokeniseAsync_WrapsNetworkFailureAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            req.RequestUri!.AbsolutePath.Contains("oauth/token", StringComparison.OrdinalIgnoreCase)
                ? StubHttpMessageHandler.Json(HttpStatusCode.OK, TokenJson)
                : throw new HttpRequestException("DNS fail"));
        var provider = CreateRaw(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task GetPaymentMethodAsync_ReturnsNull_On404()
    {
        var handler = TokenThen(_ => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "no"));
        var provider = CreateRead(handler);
        Assert.Null(await provider.GetPaymentMethodAsync("missing"));
    }

    [Fact]
    public async Task ListPaymentMethodsAsync_ReturnsMappedCards()
    {
        var handler = TokenThen(req =>
        {
            Assert.Contains("/cards", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"cards":[
                    {"cardToken":"a","customerId":"CUS-001","maskedPan":"4111********1111","expiryDate":"1228","cardScheme":"visa","defaultCard":true},
                    {"cardToken":"b","customerId":"CUS-001","maskedPan":"5500********2222","expiryDate":"0329","cardScheme":"mastercard"}
                ]}
                """);
        });
        var provider = CreateRead(handler);
        var list = new List<PaymentMethod>();
        await foreach (var pm in provider.ListPaymentMethodsAsync("CUS-001"))
            list.Add(pm);
        Assert.Equal(2, list.Count);
        Assert.Equal("1111", list[0].Last4);
        Assert.True(list[0].IsDefault);
        Assert.Equal("2222", list[1].Last4);
    }

    [Fact]
    public async Task DeletePaymentMethodAsync_ReturnsFalse_On404()
    {
        var handler = TokenThen(_ => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "no"));
        var provider = CreateRead(handler);
        Assert.False(await provider.DeletePaymentMethodAsync("missing"));
    }
}
