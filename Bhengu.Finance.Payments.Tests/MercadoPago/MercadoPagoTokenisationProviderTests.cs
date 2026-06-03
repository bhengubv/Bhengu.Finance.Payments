// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.MercadoPago.Configuration;
using Bhengu.Finance.Payments.MercadoPago.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.MercadoPago;

public class MercadoPagoTokenisationProviderTests
{
    private static MercadoPagoTokenisationProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new MercadoPagoOptions { AccessToken = "TEST-token" }),
            NullLogger<MercadoPagoTokenisationProvider>.Instance);

    private static TokeniseRequest SampleRequest(string? customerId = null) => new()
    {
        Card = new CardDetails
        {
            CardholderName = "TIAGO BENGU",
            CardNumber = "4111111111111111",
            ExpiryMonth = 12,
            ExpiryYear = 2030,
            Cvv = "123"
        },
        CustomerId = customerId,
        DisplayName = "personal-visa"
    };

    [Fact]
    public async Task TokeniseAsync_CreatesCustomer_AndAttachesCard_OnSuccess()
    {
        var calls = new List<string>();
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            calls.Add(req.RequestUri!.PathAndQuery);
            if (req.RequestUri!.PathAndQuery.Contains("/v1/card_tokens", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.Created,
                    """{"id":"CRD_TOK_abc","status":"active","last_four_digits":"1111"}""");
            if (req.RequestUri!.PathAndQuery.EndsWith("/v1/customers", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.Created,
                    """{"id":"CUST_111","email":"vault@example.com"}""");
            if (req.RequestUri!.PathAndQuery.Contains("/v1/customers/CUST_111/cards", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.Created,
                    """{"id":"CARD_999","customer_id":"CUST_111","first_six_digits":"411111","last_four_digits":"1111","expiration_month":12,"expiration_year":2030,"payment_method":{"id":"visa","name":"Visa"},"date_created":"2026-06-03T10:00:00.000-03:00"}""");
            throw new InvalidOperationException($"Unexpected: {req.RequestUri}");
        });

        var provider = Create(handler);
        var pm = await provider.TokeniseAsync(SampleRequest());

        Assert.Equal(3, calls.Count);
        Assert.Equal("CARD_999", pm.Token);
        Assert.Equal("CUST_111", pm.CustomerId);
        Assert.Equal(PaymentMethodKind.Card, pm.Kind);
        Assert.Equal("Visa", pm.Brand);
        Assert.Equal("1111", pm.Last4);
        Assert.Equal(12, pm.ExpiryMonth);
        Assert.Equal(2030, pm.ExpiryYear);
    }

    [Fact]
    public async Task TokeniseAsync_SkipsCustomerCreate_WhenCustomerIdProvided()
    {
        var calls = new List<string>();
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            calls.Add(req.RequestUri!.PathAndQuery);
            if (req.RequestUri!.PathAndQuery.Contains("/v1/card_tokens", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.Created, """{"id":"CRD_TOK_x","status":"active"}""");
            // We deliberately skip the customer creation; the next call must be the cards endpoint.
            if (req.RequestUri!.PathAndQuery.Contains("/v1/customers/CUST_PRE/cards", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.Created,
                    """{"id":"CARD_PRE","customer_id":"CUST_PRE","last_four_digits":"4242","payment_method":{"id":"visa","name":"Visa"}}""");
            throw new InvalidOperationException($"Unexpected: {req.RequestUri}");
        });

        var provider = Create(handler);
        var pm = await provider.TokeniseAsync(SampleRequest(customerId: "CUST_PRE"));

        Assert.Equal(2, calls.Count);
        Assert.Equal("CARD_PRE", pm.Token);
        Assert.Equal("CUST_PRE", pm.CustomerId);
    }

    [Fact]
    public async Task TokeniseAsync_Throws_On4xx()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "card declined"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task GetPaymentMethodAsync_Returns_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("/v1/customers/cards/CARD_999", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"CARD_999","customer_id":"CUST_111","last_four_digits":"1111","payment_method":{"id":"visa","name":"Visa"}}""");
        });
        var provider = Create(handler);
        var pm = await provider.GetPaymentMethodAsync("CARD_999");
        Assert.NotNull(pm);
        Assert.Equal("CARD_999", pm!.Token);
        Assert.Equal("CUST_111", pm.CustomerId);
        Assert.Equal("1111", pm.Last4);
    }

    [Fact]
    public async Task GetPaymentMethodAsync_ReturnsNull_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "no such card"));
        var provider = Create(handler);
        Assert.Null(await provider.GetPaymentMethodAsync("missing"));
    }

    [Fact]
    public async Task ListPaymentMethodsAsync_DeserialisesArray()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("/v1/customers/CUST_111/cards", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                [
                  {"id":"CARD_1","last_four_digits":"1111","payment_method":{"id":"visa","name":"Visa"}},
                  {"id":"CARD_2","last_four_digits":"4242","payment_method":{"id":"master","name":"Mastercard"}}
                ]
                """);
        });
        var provider = Create(handler);
        var list = await provider.ListPaymentMethodsAsync("CUST_111");
        Assert.Equal(2, list.Count);
        Assert.Equal("CARD_1", list[0].Token);
        Assert.Equal("Mastercard", list[1].Brand);
    }

    [Fact]
    public async Task DeletePaymentMethodAsync_DeletesScopedToCustomer()
    {
        var deleted = false;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Get)
                return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                    """{"id":"CARD_999","customer_id":"CUST_111","last_four_digits":"1111","payment_method":{"id":"visa","name":"Visa"}}""");
            if (req.Method == HttpMethod.Delete)
            {
                deleted = true;
                Assert.Contains("/v1/customers/CUST_111/cards/CARD_999", req.RequestUri!.PathAndQuery);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            throw new InvalidOperationException("Unexpected request");
        });
        var provider = Create(handler);
        Assert.True(await provider.DeletePaymentMethodAsync("CARD_999"));
        Assert.True(deleted);
    }

    [Fact]
    public async Task DeletePaymentMethodAsync_ReturnsFalse_WhenMissing()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            req.Method == HttpMethod.Get
                ? StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "no card")
                : throw new InvalidOperationException("DELETE should not run"));
        var provider = Create(handler);
        Assert.False(await provider.DeletePaymentMethodAsync("missing"));
    }
}
