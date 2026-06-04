// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Paytm.Configuration;
using Bhengu.Finance.Payments.Paytm.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paytm;

public class PaytmTokenisationProviderTests
{
    private static PaytmTokenisationProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new PaytmOptions { MerchantId = "MID1", MerchantKey = "secret_key" }),
            NullLogger<PaytmTokenisationProvider>.Instance);

    private static TokeniseRequest SampleRequest() => new()
    {
        Card = new CardDetails
        {
            CardholderName = "Alice",
            CardNumber = "4012001037141112",
            ExpiryMonth = 11,
            ExpiryYear = 2030,
            Cvv = "123"
        },
        CustomerId = "cust-1",
        DisplayName = "Alice's card"
    };

    [Fact]
    public async Task TokeniseAsync_ReturnsPaymentMethod_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("theia/api/v2/vault/tokeniseCard", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"body":{"resultInfo":{"resultStatus":"S","resultCode":"01"},"cardToken":"tkn-1","cardScheme":"VISA"}}
                """);
        });
        var provider = Create(handler);

        var pm = await provider.TokeniseAsync(SampleRequest());
        Assert.Equal("tkn-1", pm.Token);
        Assert.Equal("VISA", pm.Brand);
        Assert.Equal("1112", pm.Last4);
        Assert.Equal("cust-1", pm.CustomerId);
        Assert.Equal(PaymentMethodKind.Card, pm.Kind);
    }

    [Fact]
    public async Task TokeniseAsync_OnFailureResult_ThrowsDeclined()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"body":{"resultInfo":{"resultStatus":"F","resultCode":"401","resultMsg":"Card declined"}}}
                """));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task GetPaymentMethodAsync_ReturnsCached_AfterTokenise()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"body":{"resultInfo":{"resultStatus":"S"},"cardToken":"tkn-2","cardScheme":"MASTERCARD"}}
                """));
        var provider = Create(handler);
        var pm = await provider.TokeniseAsync(SampleRequest());

        var fetched = await provider.GetPaymentMethodAsync(pm.Token);
        Assert.NotNull(fetched);
        Assert.Equal(pm.Token, fetched!.Token);
    }

    [Fact]
    public async Task GetPaymentMethodAsync_ReturnsNull_WhenUnknown()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        Assert.Null(await provider.GetPaymentMethodAsync($"missing-{Guid.NewGuid():N}"));
    }

    [Fact]
    public async Task ListPaymentMethodsAsync_ReturnsAllForCustomer()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"body":{"resultInfo":{"resultStatus":"S"},"cardToken":"tkn-3","cardScheme":"VISA"}}
                """));
        var provider = Create(handler);
        await provider.TokeniseAsync(SampleRequest() with { CustomerId = "shared_cust" });
        await provider.TokeniseAsync(new TokeniseRequest
        {
            Card = SampleRequest().Card,
            CustomerId = "shared_cust",
            DisplayName = "second card"
        });

        var list = await provider.ListPaymentMethodsAsync("shared_cust");
        Assert.NotEmpty(list);
        Assert.All(list, m => Assert.Equal("shared_cust", m.CustomerId));
    }

    [Fact]
    public async Task DeletePaymentMethodAsync_RemovesFromCache()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"body":{"resultInfo":{"resultStatus":"S"},"cardToken":"tkn-4","cardScheme":"VISA"}}
                """));
        var provider = Create(handler);
        var pm = await provider.TokeniseAsync(SampleRequest());

        Assert.True(await provider.DeletePaymentMethodAsync(pm.Token));
        Assert.Null(await provider.GetPaymentMethodAsync(pm.Token));
    }

    [Fact]
    public async Task TokeniseAsync_OnNetworkFailure_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.TokeniseAsync(SampleRequest()));
    }
}
