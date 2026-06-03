// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.Yoco.Configuration;
using Bhengu.Finance.Payments.Yoco.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Yoco;

public class YocoTokenisationProviderTests
{
    private static YocoTokenisationProvider Create(StubHttpMessageHandler handler, YocoTokenCache? cache = null)
    {
        var opts = new YocoOptions { SecretKey = "sk_test_xx", WebhookSecret = "ws" };
        var http = new HttpClient(handler);
        return new YocoTokenisationProvider(
            http,
            Options.Create(opts),
            NullLogger<YocoTokenisationProvider>.Instance,
            cache ?? new YocoTokenCache());
    }

    private static TokeniseRequest SampleRequest() => new()
    {
        Card = new CardDetails
        {
            CardholderName = "T Bengu",
            CardNumber = "4242424242424242",
            ExpiryMonth = 12,
            ExpiryYear = 2030,
            Cvv = "123"
        },
        CustomerId = "cust-1",
        DisplayName = "Work card"
    };

    [Fact]
    public async Task TokeniseAsync_PostsToCheckoutsAndReturnsCardMethod()
    {
        string? capturedPath = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            capturedPath = req.RequestUri!.PathAndQuery;
            Assert.Equal(HttpMethod.Post, req.Method);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"checkout-abc","redirectUrl":"https://yoco.com/pay/checkout-abc","amount":100,"currency":"ZAR","status":"open"}
                """);
        });
        var cache = new YocoTokenCache();
        var provider = Create(handler, cache);

        var method = await provider.TokeniseAsync(SampleRequest());

        Assert.NotNull(capturedPath);
        Assert.Contains("checkouts", capturedPath);
        Assert.Equal("checkout-abc", method.Token);
        Assert.Equal(PaymentMethodKind.Card, method.Kind);
        Assert.Equal("cust-1", method.CustomerId);
        Assert.Equal("Work card", method.DisplayName);

        // Cached for subsequent GetPaymentMethodAsync.
        var cached = cache.TryGet("checkout-abc");
        Assert.NotNull(cached);
    }

    [Fact]
    public async Task TokeniseAsync_Throws4xxAsPaymentDeclined()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "invalid")));
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_Throws5xxAsProviderUnavailable()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down")));
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_Throws429AsRateLimit()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "slow")));
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task GetPaymentMethodAsync_ReturnsCachedMethod()
    {
        var cache = new YocoTokenCache();
        cache.Set(new PaymentMethod { Token = "tok-cached", Kind = PaymentMethodKind.Card });
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), cache);

        var result = await provider.GetPaymentMethodAsync("tok-cached");
        Assert.NotNull(result);
        Assert.Equal("tok-cached", result!.Token);
    }

    [Fact]
    public async Task GetPaymentMethodAsync_ReturnsNullForUnknownToken()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var result = await provider.GetPaymentMethodAsync("tok-unknown");
        Assert.Null(result);
    }

    [Fact]
    public async Task ListPaymentMethodsAsync_ReturnsEmptyList()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var list = await provider.ListPaymentMethodsAsync("cust-1");
        Assert.Empty(list);
    }

    [Fact]
    public async Task DeletePaymentMethodAsync_ReturnsTrueAndEvictsCache()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Delete, req.Method);
            Assert.Contains("cards/", req.RequestUri!.PathAndQuery);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var cache = new YocoTokenCache();
        cache.Set(new PaymentMethod { Token = "tok-del", Kind = PaymentMethodKind.Card });
        var provider = Create(handler, cache);

        var deleted = await provider.DeletePaymentMethodAsync("tok-del");
        Assert.True(deleted);
        Assert.Null(cache.TryGet("tok-del"));
    }
}
