// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Internals;
using Bhengu.Finance.Payments.Paystack.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paystack;

public class PaystackTokenisationProviderTests
{
    private static PaystackTokenisationProvider Create(StubHttpMessageHandler handler, PaystackOptions? opts = null)
    {
        opts ??= new PaystackOptions
        {
            SecretKey = "sk_test_xx",
            WebhookSecret = "webhook-test-secret",
            DefaultEmail = "buyer@example.com"
        };
        var http = new HttpClient(handler);
        return new PaystackTokenisationProvider(
            http,
            Options.Create(opts),
            NullLogger<PaystackTokenisationProvider>.Instance,
            new PaystackIdempotencyCache());
    }

    private static TokeniseRequest SampleRequest(string? idempotencyKey = null) => new()
    {
        Card = new CardDetails
        {
            CardholderName = "Test Buyer",
            CardNumber = "4084084084084081",
            ExpiryMonth = 12,
            ExpiryYear = 2030,
            Cvv = "408"
        },
        DisplayName = "Personal card",
        SetAsDefault = true,
        IdempotencyKey = idempotencyKey
    };

    [Fact]
    public void Constructor_Throws_WhenSecretKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() => new PaystackTokenisationProvider(
            http,
            Options.Create(new PaystackOptions()),
            NullLogger<PaystackTokenisationProvider>.Instance,
            new PaystackIdempotencyCache()));
    }

    [Fact]
    public async Task TokeniseAsync_ReturnsPaymentMethod_OnSuccess()
    {
        var step = 0;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            step++;
            if (req.RequestUri!.PathAndQuery.Contains("customer", StringComparison.Ordinal) && step == 1)
            {
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"status":true,"data":{"customer_code":"CUS_abc","email":"buyer@example.com"}}
                    """);
            }
            Assert.Contains("charge", req.RequestUri.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"message":"ok","data":{"status":"success","reference":"ref_psk_token","authorization":{"authorization_code":"AUTH_xyz","brand":"visa","last4":"4081","exp_month":"12","exp_year":"2030","channel":"card"}}}
                """);
        });

        var provider = Create(handler);
        var pm = await provider.TokeniseAsync(SampleRequest());

        Assert.Equal("AUTH_xyz", pm.Token);
        Assert.Equal("CUS_abc", pm.CustomerId);
        Assert.Equal(PaymentMethodKind.Card, pm.Kind);
        Assert.Equal("visa", pm.Brand);
        Assert.Equal("4081", pm.Last4);
        Assert.Equal(12, pm.ExpiryMonth);
        Assert.Equal(2030, pm.ExpiryYear);
        Assert.True(pm.IsDefault);
    }

    [Fact]
    public async Task TokeniseAsync_ThrowsPaymentDeclined_WhenNoAuthorizationCode()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("customer", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":true,"data":{"customer_code":"CUS_x","email":"buyer@example.com"}}""");
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"message":"declined","data":{"status":"failed","authorization":{}}}
                """);
        });
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate limited"));
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
    public async Task TokeniseAsync_ThrowsProviderUnavailable_OnNetworkError()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS failure"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task ListPaymentMethodsAsync_ReturnsEmpty_WhenCustomerHasNoAuthorizations()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":true,"data":{"customer_code":"CUS_a","authorizations":[]}}"""));
        var provider = Create(handler);
        var methods = await provider.ListPaymentMethodsAsync("CUS_a");
        Assert.Empty(methods);
    }

    [Fact]
    public async Task ListPaymentMethodsAsync_ReturnsMappedAuthorizations()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"data":{"customer_code":"CUS_b","authorizations":[
                    {"authorization_code":"AUTH_1","brand":"visa","last4":"1234","exp_month":"01","exp_year":"2029","channel":"card"},
                    {"authorization_code":"AUTH_2","brand":"mastercard","last4":"5678","exp_month":"06","exp_year":"2031","channel":"bank"}
                ]}}
                """));
        var provider = Create(handler);
        var methods = await provider.ListPaymentMethodsAsync("CUS_b");
        Assert.Equal(2, methods.Count);
        Assert.Equal(PaymentMethodKind.Card, methods[0].Kind);
        Assert.Equal(PaymentMethodKind.BankAccount, methods[1].Kind);
        Assert.Equal(2031, methods[1].ExpiryYear);
    }

    [Fact]
    public async Task DeletePaymentMethodAsync_ReturnsTrue_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("deactivate_authorization", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":true,"message":"Authorization deactivated"}""");
        });
        var provider = Create(handler);
        Assert.True(await provider.DeletePaymentMethodAsync("AUTH_anything"));
    }
}
