// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.PagSeguro.Configuration;
using Bhengu.Finance.Payments.PagSeguro.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PagSeguro;

public class PagSeguroTokenisationProviderTests
{
    private static PagSeguroTokenisationProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new PagSeguroOptions { ApiToken = "pagbank-test-token" }),
            NullLogger<PagSeguroTokenisationProvider>.Instance);

    private static TokeniseRequest SampleRequest() => new()
    {
        Card = new CardDetails
        {
            CardholderName = "TIAGO BENGU",
            CardNumber = "4111111111111111",
            ExpiryMonth = 12,
            ExpiryYear = 2030,
            Cvv = "123"
        }
    };

    [Fact]
    public async Task TokeniseAsync_PostsToken_AndReturnsPaymentMethod()
    {
        string? sentBody = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/tokens", req.RequestUri!.PathAndQuery);
            sentBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.Created, """
                {"id":"TOK_abc","type":"card","created_at":"2026-06-03T10:00:00-03:00","card":{"brand":"visa","last4":"1111","exp_month":12,"exp_year":2030}}
                """);
        });
        var provider = Create(handler);

        var pm = await provider.TokeniseAsync(SampleRequest());

        Assert.Equal("TOK_abc", pm.Token);
        Assert.Equal(PaymentMethodKind.Card, pm.Kind);
        Assert.Equal("visa", pm.Brand);
        Assert.Equal("1111", pm.Last4);
        Assert.Equal(12, pm.ExpiryMonth);
        Assert.Equal(2030, pm.ExpiryYear);
        Assert.NotNull(sentBody);
        Assert.Contains("\"number\":\"4111111111111111\"", sentBody!);
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
            Assert.Contains("/tokens/TOK_abc", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"TOK_abc","type":"card","card":{"brand":"visa","last4":"1111","exp_month":12,"exp_year":2030}}
                """);
        });
        var provider = Create(handler);
        var pm = await provider.GetPaymentMethodAsync("TOK_abc");
        Assert.NotNull(pm);
        Assert.Equal("TOK_abc", pm!.Token);
        Assert.Equal("visa", pm.Brand);
    }

    [Fact]
    public async Task GetPaymentMethodAsync_ReturnsNull_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.Null(await provider.GetPaymentMethodAsync("missing"));
    }

    [Fact]
    public async Task ListPaymentMethodsAsync_ReturnsEmpty_BecausePagBankHasNoListEndpoint()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        });
        var provider = Create(handler);
        var list = await provider.ListPaymentMethodsAsync("CUST_1");
        Assert.Empty(list);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DeletePaymentMethodAsync_ReturnsTrue_OnSuccess()
    {
        var deleted = false;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Delete, req.Method);
            Assert.Contains("/tokens/TOK_abc", req.RequestUri!.PathAndQuery);
            deleted = true;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var provider = Create(handler);
        Assert.True(await provider.DeletePaymentMethodAsync("TOK_abc"));
        Assert.True(deleted);
    }

    [Fact]
    public async Task DeletePaymentMethodAsync_ReturnsFalse_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.False(await provider.DeletePaymentMethodAsync("missing"));
    }
}
