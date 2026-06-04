// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Stripe;

[Collection(StripeConfigurationCollection.Name)]
public class StripeTokenisationProviderTests
{
    private static StripeTokenisationProvider Create(StubHttpMessageHandler handler, StripeOptions? opts = null) =>
        new(new HttpClient(handler),
            Options.Create(opts ?? new StripeOptions { SecretKey = "sk_test_fake", WebhookSecret = "whsec_test_fake" }),
            NullLogger<StripeTokenisationProvider>.Instance);

    private static StripeRawCardTokenisationProvider CreateRaw(StubHttpMessageHandler handler, StripeOptions? opts = null) =>
        new(new HttpClient(handler),
            Options.Create(opts ?? new StripeOptions { SecretKey = "sk_test_fake", WebhookSecret = "whsec_test_fake" }),
            NullLogger<StripeRawCardTokenisationProvider>.Instance);

    private static TokeniseRequest SampleRequest(string? customerId = null, string? idempotencyKey = null) => new()
    {
        Card = new CardDetails
        {
            CardholderName = "T Bengu",
            CardNumber = "4242424242424242",
            ExpiryMonth = 12,
            ExpiryYear = 2030,
            Cvv = "123"
        },
        CustomerId = customerId,
        IdempotencyKey = idempotencyKey
    };

    [Fact]
    public void Constructor_Throws_WhenSecretKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ProviderConfigurationException>(() =>
            new StripeTokenisationProvider(http, Options.Create(new StripeOptions()), NullLogger<StripeTokenisationProvider>.Instance));
    }

    [Fact]
    public async Task TokeniseAsync_NewCustomer_CreatesCustomerAndPaymentMethod()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("payment_methods", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"id":"pm_test_1","object":"payment_method","type":"card","card":{"brand":"visa","last4":"4242","exp_month":12,"exp_year":2030},"created":1700000000}
                    """);
            }
            // Customer create
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"cus_test_1","object":"customer","email":"a@b.com"}
                """);
        });
        var provider = CreateRaw(handler);

        var result = await provider.TokeniseAsync(SampleRequest());

        Assert.Equal("pm_test_1", result.Token);
        Assert.Equal("cus_test_1", result.CustomerId);
        Assert.Equal(PaymentMethodKind.Card, result.Kind);
        Assert.Equal("visa", result.Brand);
        Assert.Equal("4242", result.Last4);
        Assert.Equal(12, result.ExpiryMonth);
        Assert.Equal(2030, result.ExpiryYear);
    }

    [Fact]
    public async Task TokeniseAsync_ExistingCustomer_AttachesPaymentMethod()
    {
        var attached = false;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("/attach", StringComparison.Ordinal))
            {
                attached = true;
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"id":"pm_test_2","object":"payment_method","type":"card","card":{"brand":"visa","last4":"4242","exp_month":12,"exp_year":2030},"customer":"cus_existing"}
                    """);
            }
            // Create PM
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"pm_test_2","object":"payment_method","type":"card","card":{"brand":"visa","last4":"4242","exp_month":12,"exp_year":2030}}
                """);
        });
        var provider = CreateRaw(handler);

        var result = await provider.TokeniseAsync(SampleRequest(customerId: "cus_existing"));

        Assert.True(attached);
        Assert.Equal("pm_test_2", result.Token);
        Assert.Equal("cus_existing", result.CustomerId);
    }

    [Fact]
    public async Task TokeniseAsync_Throws4xxAsPaymentDeclined()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.PaymentRequired, """
            {"error":{"type":"card_error","code":"card_declined","message":"Your card was declined."}}
            """));
        var provider = CreateRaw(handler);
        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.TokeniseAsync(SampleRequest()));
        Assert.Equal("card_declined", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task TokeniseAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.TooManyRequests, """
            {"error":{"type":"invalid_request_error","message":"Too many requests"}}
            """));
        var provider = CreateRaw(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_Throws5xxAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.BadGateway, """
            {"error":{"type":"api_error","message":"Stripe is down"}}
            """));
        var provider = CreateRaw(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task GetPaymentMethodAsync_ReturnsMappedDescriptor()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"id":"pm_get_1","object":"payment_method","type":"card","card":{"brand":"mastercard","last4":"4444","exp_month":6,"exp_year":2028},"customer":"cus_x"}
            """));
        var provider = Create(handler);
        var pm = await provider.GetPaymentMethodAsync("pm_get_1");
        Assert.NotNull(pm);
        Assert.Equal("mastercard", pm!.Brand);
        Assert.Equal("4444", pm.Last4);
    }

    [Fact]
    public async Task GetPaymentMethodAsync_ReturnsNull_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.NotFound, """
            {"error":{"type":"invalid_request_error","message":"No such payment method"}}
            """));
        var provider = Create(handler);
        Assert.Null(await provider.GetPaymentMethodAsync("pm_missing"));
    }

    [Fact]
    public async Task DeletePaymentMethodAsync_ReturnsTrue_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("/detach", req.RequestUri!.PathAndQuery, StringComparison.Ordinal);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"pm_del_1","object":"payment_method","type":"card"}
                """);
        });
        var provider = Create(handler);
        Assert.True(await provider.DeletePaymentMethodAsync("pm_del_1"));
    }

    [Fact]
    public async Task DeletePaymentMethodAsync_ReturnsFalse_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.NotFound, """
            {"error":{"type":"invalid_request_error","message":"No such payment method"}}
            """));
        var provider = Create(handler);
        Assert.False(await provider.DeletePaymentMethodAsync("pm_missing"));
    }
}
