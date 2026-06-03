// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Bhengu.Finance.Payments.Razorpay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Razorpay;

public class RazorpayTokenisationProviderTests
{
    private static RazorpayTokenisationProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new RazorpayOptions { KeyId = "rzp_test_x", KeySecret = "secret_x" }),
            NullLogger<RazorpayTokenisationProvider>.Instance);

    private static TokeniseRequest SampleRequest(string? customerId = null) => new()
    {
        Card = new CardDetails
        {
            CardholderName = "T Bhengu",
            CardNumber = "4111111111111111",
            ExpiryMonth = 12,
            ExpiryYear = 2030,
            Cvv = "123"
        },
        CustomerId = customerId,
        DisplayName = "Personal Visa"
    };

    [Fact]
    public void ProviderName_IsRazorpay()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("razorpay", provider.ProviderName);
    }

    [Fact]
    public async Task TokeniseAsync_CreatesCustomerAndToken_OnSuccess()
    {
        var calls = new List<string>();
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            calls.Add(req.RequestUri!.PathAndQuery);
            if (req.RequestUri!.PathAndQuery.Contains("v1/customers", StringComparison.Ordinal) && !req.RequestUri!.PathAndQuery.Contains("/tokens", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"cust_abc","entity":"customer","name":"T Bhengu"}""");
            if (req.RequestUri!.PathAndQuery.Contains("v1/tokens", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"token_xyz","entity":"token","customer_id":"cust_abc","method":"card","card":{"last4":"1111","network":"Visa","expiry_month":12,"expiry_year":2030}}""");
            throw new InvalidOperationException($"Unexpected: {req.RequestUri}");
        });

        var provider = Create(handler);
        var method = await provider.TokeniseAsync(SampleRequest());

        Assert.Equal(2, calls.Count);
        Assert.Equal("token_xyz", method.Token);
        Assert.Equal("cust_abc", method.CustomerId);
        Assert.Equal(PaymentMethodKind.Card, method.Kind);
        Assert.Equal("Visa", method.Brand);
        Assert.Equal("1111", method.Last4);
        Assert.Equal(12, method.ExpiryMonth);
        Assert.Equal(2030, method.ExpiryYear);
    }

    [Fact]
    public async Task TokeniseAsync_SkipsCustomerCreation_WhenCustomerIdProvided()
    {
        var calls = new List<string>();
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            calls.Add(req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"token_def","entity":"token","customer_id":"cust_pre","method":"card","card":{"last4":"4242","network":"Visa"}}""");
        });

        var provider = Create(handler);
        var method = await provider.TokeniseAsync(SampleRequest(customerId: "cust_pre"));

        Assert.Single(calls);
        Assert.Contains("v1/tokens", calls[0]);
        Assert.Equal("token_def", method.Token);
        Assert.Equal("cust_pre", method.CustomerId);
    }

    [Fact]
    public async Task TokeniseAsync_Throws_OnBadRequest()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, """{"error":{"code":"BAD_REQUEST_ERROR","description":"Invalid card"}}"""));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_Throws_OnRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "throttled"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_Throws_OnNetworkFailure()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("connect timeout"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.TokeniseAsync(SampleRequest(customerId: "cust_pre")));
    }

    [Fact]
    public async Task GetPaymentMethodAsync_Returns_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v1/tokens/token_xyz", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"token_xyz","entity":"token","customer_id":"cust_abc","method":"card","card":{"last4":"1111","network":"Visa"}}""");
        });
        var provider = Create(handler);
        var method = await provider.GetPaymentMethodAsync("token_xyz");

        Assert.NotNull(method);
        Assert.Equal("token_xyz", method!.Token);
        Assert.Equal("cust_abc", method.CustomerId);
    }

    [Fact]
    public async Task GetPaymentMethodAsync_ReturnsNull_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.NotFound, """{"error":{"code":"BAD_REQUEST_ERROR","description":"Token not found"}}"""));
        var provider = Create(handler);
        Assert.Null(await provider.GetPaymentMethodAsync("token_missing"));
    }

    [Fact]
    public async Task ListPaymentMethodsAsync_DeserialisesCollection()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v1/customers/cust_abc/tokens", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"entity":"collection","count":2,"items":[
                  {"id":"token_1","entity":"token","method":"card","card":{"last4":"1111","network":"Visa"}},
                  {"id":"token_2","entity":"token","method":"upi","wallet":"upi"}
                ]}
                """);
        });
        var provider = Create(handler);
        var methods = await provider.ListPaymentMethodsAsync("cust_abc");

        Assert.Equal(2, methods.Count);
        Assert.Equal("token_1", methods[0].Token);
        Assert.Equal(PaymentMethodKind.Card, methods[0].Kind);
        Assert.Equal(PaymentMethodKind.Wallet, methods[1].Kind);
    }

    [Fact]
    public async Task DeletePaymentMethodAsync_ReturnsTrue_OnSuccess()
    {
        var deleteCalled = false;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.PathAndQuery.Contains("/tokens/"))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"token_xyz","entity":"token","customer_id":"cust_abc","method":"card","card":{"last4":"1111"}}""");
            if (req.Method == HttpMethod.Delete)
            {
                deleteCalled = true;
                Assert.Contains("v1/customers/cust_abc/tokens/token_xyz", req.RequestUri!.PathAndQuery);
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"deleted":true}""");
            }
            throw new InvalidOperationException("Unexpected request");
        });
        var provider = Create(handler);
        Assert.True(await provider.DeletePaymentMethodAsync("token_xyz"));
        Assert.True(deleteCalled);
    }

    [Fact]
    public async Task DeletePaymentMethodAsync_ReturnsFalse_WhenTokenNotFound()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
            req.Method == HttpMethod.Get
                ? StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "not found")
                : throw new InvalidOperationException("Should not reach DELETE"));
        var provider = Create(handler);
        Assert.False(await provider.DeletePaymentMethodAsync("token_nope"));
    }
}
