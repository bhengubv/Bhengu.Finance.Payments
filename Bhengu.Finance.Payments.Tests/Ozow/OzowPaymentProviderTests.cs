// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Ozow.Configuration;
using Bhengu.Finance.Payments.Ozow.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Ozow;

public class OzowPaymentProviderTests
{
    private static OzowPaymentProvider Create(StubHttpMessageHandler handler, OzowOptions? opts = null)
    {
        opts ??= new OzowOptions { SiteCode = "TEST", PrivateKey = "priv", ApiKey = "apik", UseSandbox = true };
        var http = new HttpClient(handler);
        return new OzowPaymentProvider(http, Options.Create(opts), NullLogger<OzowPaymentProvider>.Instance);
    }

    private static PaymentRequest SampleRequest() => new()
    {
        PaymentMethodToken = "ozow-token",
        Amount = 250m,
        Currency = "ZAR",
        Description = "Ozow test"
    };

    [Fact]
    public void Constructor_Throws_WhenSiteCodeMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new OzowPaymentProvider(http, Options.Create(new OzowOptions { PrivateKey = "x", ApiKey = "y" }), NullLogger<OzowPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenPrivateKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new OzowPaymentProvider(http, Options.Create(new OzowOptions { SiteCode = "x", ApiKey = "y" }), NullLogger<OzowPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenApiKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new OzowPaymentProvider(http, Options.Create(new OzowOptions { SiteCode = "x", PrivateKey = "y" }), NullLogger<OzowPaymentProvider>.Instance));
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
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad request"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SampleRequest()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("network down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SampleRequest()));
    }
}
