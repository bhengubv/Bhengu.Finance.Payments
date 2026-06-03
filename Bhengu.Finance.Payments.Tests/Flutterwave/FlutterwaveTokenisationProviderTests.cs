// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Bhengu.Finance.Payments.Flutterwave.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Flutterwave;

public class FlutterwaveTokenisationProviderTests
{
    private static FlutterwaveTokenisationProvider Create(StubHttpMessageHandler handler, FlutterwaveOptions? opts = null)
    {
        opts ??= new FlutterwaveOptions
        {
            SecretKey = "FLWSECK_TEST-xxx",
            PublicKey = "FLWPUBK_TEST-xxx",
            EncryptionKey = "FLWSECK_TEST_enc"
        };
        var http = new HttpClient(handler);
        return new FlutterwaveTokenisationProvider(http, Options.Create(opts), NullLogger<FlutterwaveTokenisationProvider>.Instance);
    }

    private static TokeniseRequest SampleRequest() => new()
    {
        Card = new CardDetails
        {
            CardholderName = "Test Buyer",
            CardNumber = "4111111111111111",
            ExpiryMonth = 9,
            ExpiryYear = 2030,
            Cvv = "123"
        },
        CustomerId = "buyer@example.com",
        SetAsDefault = true,
        DisplayName = "Default card"
    };

    [Fact]
    public async Task TokeniseAsync_Returns_VaultedPaymentMethod_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v3/tokenized-charges", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","message":"Charge initiated","data":{"id":1,"tx_ref":"vault-1","status":"successful","card":{"token":"flw-tok_abc","first_6digits":"411111","last_4digits":"1111","type":"VISA","expiry":"09/30"}}}
                """);
        });
        var provider = Create(handler);

        var pm = await provider.TokeniseAsync(SampleRequest());

        Assert.Equal("flw-tok_abc", pm.Token);
        Assert.Equal("buyer@example.com", pm.CustomerId);
        Assert.Equal(PaymentMethodKind.Card, pm.Kind);
        Assert.Equal("VISA", pm.Brand);
        Assert.Equal("1111", pm.Last4);
        Assert.True(pm.IsDefault);
    }

    [Fact]
    public async Task TokeniseAsync_Throws_WhenCustomerIdMissing()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.TokeniseAsync(new TokeniseRequest
        {
            Card = new CardDetails
            {
                CardholderName = "x", CardNumber = "4111111111111111", ExpiryMonth = 1, ExpiryYear = 2030, Cvv = "111"
            }
        }));
    }

    [Fact]
    public async Task TokeniseAsync_Throws_WhenEncryptionKeyMissing()
    {
        var provider = Create(
            new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")),
            new FlutterwaveOptions { SecretKey = "k" });
        await Assert.ThrowsAsync<ProviderConfigurationException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_Wraps4xxAsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "card declined"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_Wraps5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS fail"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_Throws_WhenResponseMissesToken()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"status":"success","data":{"id":1,"card":{"type":"VISA"}}}
            """));
        var provider = Create(handler);
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.TokeniseAsync(SampleRequest()));
        Assert.Contains("card.token", ex.Message);
    }

    [Fact]
    public async Task GetPaymentMethodAsync_ReturnsStub_ForKnownToken()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var pm = await provider.GetPaymentMethodAsync("flw-tok_abc");
        Assert.NotNull(pm);
        Assert.Equal("flw-tok_abc", pm!.Token);
        Assert.Equal(PaymentMethodKind.Card, pm.Kind);
    }

    [Fact]
    public async Task ListPaymentMethodsAsync_ReturnsEmpty_NoCustomerLookupSupported()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var list = await provider.ListPaymentMethodsAsync("buyer@example.com");
        Assert.Empty(list);
    }

    [Fact]
    public async Task DeletePaymentMethodAsync_ReturnsTrue_ForKnownToken()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(await provider.DeletePaymentMethodAsync("flw-tok_abc"));
    }
}
