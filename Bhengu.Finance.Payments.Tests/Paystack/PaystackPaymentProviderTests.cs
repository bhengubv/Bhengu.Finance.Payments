// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paystack;

public class PaystackPaymentProviderTests
{
    private static PaystackPaymentProvider Create(StubHttpMessageHandler handler, PaystackOptions? opts = null)
    {
        opts ??= new PaystackOptions { SecretKey = "sk_test_xx", DefaultEmail = "buyer@example.com" };
        var http = new HttpClient(handler);
        return new PaystackPaymentProvider(http, Options.Create(opts), NullLogger<PaystackPaymentProvider>.Instance);
    }

    private static PaymentRequest SampleRequest() => new()
    {
        PaymentMethodToken = "AUTH_abc123",
        Amount = 100m,
        Currency = "NGN",
        Description = "Paystack test"
    };

    [Fact]
    public void Constructor_Throws_WhenSecretKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new PaystackPaymentProvider(http, Options.Create(new PaystackOptions()), NullLogger<PaystackPaymentProvider>.Instance));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws_WhenEmailMissing()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler, new PaystackOptions { SecretKey = "sk_x", DefaultEmail = null! });
        var request = new PaymentRequest
        {
            PaymentMethodToken = "AUTH_x",
            Amount = 10m,
            Currency = "NGN",
            Description = "no-email"
        };
        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(request));
        Assert.Equal("missing_email", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate limited"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPaymentAsync(SampleRequest()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "invalid auth"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SampleRequest()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.InternalServerError, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SampleRequest()));
    }
}
