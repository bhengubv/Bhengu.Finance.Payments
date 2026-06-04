// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.PayUIndia.Configuration;
using Bhengu.Finance.Payments.PayUIndia.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayUIndia;

public class PayUIndiaTokenisationProviderTests
{
    private static PayUIndiaTokenisationProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new PayUIndiaOptions
            {
                MerchantKey = "gtKFFx",
                Salt = "eCwWELxi"
            }),
            NullLogger<PayUIndiaTokenisationProvider>.Instance);

    private static TokeniseRequest SampleRequest() => new()
    {
        Card = new CardDetails
        {
            CardholderName = "Test User",
            CardNumber = "4012001037141112",
            ExpiryMonth = 12,
            ExpiryYear = 2030,
            Cvv = "123"
        },
        CustomerId = "merchant:cust1",
        DisplayName = "My card"
    };

    [Fact]
    public async Task TokeniseAsync_ReturnsPaymentMethod_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("merchant/postservice.php", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"1","card_token":"vaulted_token_1","card_mode":"VISA","user_credentials":"merchant:cust1"}
                """);
        });
        var provider = Create(handler);

        var pm = await provider.TokeniseAsync(SampleRequest());

        Assert.Equal("vaulted_token_1", pm.Token);
        Assert.Equal("merchant:cust1", pm.CustomerId);
        Assert.Equal(PaymentMethodKind.Card, pm.Kind);
        Assert.Equal("VISA", pm.Brand);
        Assert.Equal("1112", pm.Last4);
        Assert.Equal(12, pm.ExpiryMonth);
        Assert.Equal(2030, pm.ExpiryYear);
        Assert.Equal("My card", pm.DisplayName);
    }

    [Fact]
    public async Task GetPaymentMethodAsync_ReturnsPaymentMethod_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"1","card_token":"vaulted_token_1","card_mode":"MASTERCARD","user_credentials":"cust1","card_no":"XXXXXXXXXXXX1234","expiry_month":11,"expiry_year":2029}
                """));
        var provider = Create(handler);

        var pm = await provider.GetPaymentMethodAsync("vaulted_token_1");

        Assert.NotNull(pm);
        Assert.Equal("vaulted_token_1", pm!.Token);
        Assert.Equal("MASTERCARD", pm.Brand);
        Assert.Equal("1234", pm.Last4);
        Assert.Equal(11, pm.ExpiryMonth);
        Assert.Equal(2029, pm.ExpiryYear);
    }

    [Fact]
    public async Task ListPaymentMethodsAsync_ReturnsAll()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"1","user_cards":[
                  {"card_token":"t1","card_mode":"VISA","card_no":"XXXXXXXXXXXX1111","expiry_month":1,"expiry_year":2028},
                  {"card_token":"t2","card_mode":"MASTERCARD","card_no":"XXXXXXXXXXXX2222","expiry_month":2,"expiry_year":2029}
                ]}
                """));
        var provider = Create(handler);

        var list = await provider.ListPaymentMethodsAsync("cust1");

        Assert.Equal(2, list.Count);
        Assert.Equal("t1", list[0].Token);
        Assert.Equal("1111", list[0].Last4);
        Assert.Equal("t2", list[1].Token);
        Assert.Equal("2222", list[1].Last4);
    }

    [Fact]
    public async Task DeletePaymentMethodAsync_ReturnsTrue_WhenStatusSuccess()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"1"}"""));
        var provider = Create(handler);
        Assert.True(await provider.DeletePaymentMethodAsync("vaulted_token_1"));
    }

    [Fact]
    public async Task DeletePaymentMethodAsync_ReturnsFalse_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.False(await provider.DeletePaymentMethodAsync("missing"));
    }

    [Fact]
    public async Task TokeniseAsync_OnNetworkFailure_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.TokeniseAsync(SampleRequest()));
    }
}
