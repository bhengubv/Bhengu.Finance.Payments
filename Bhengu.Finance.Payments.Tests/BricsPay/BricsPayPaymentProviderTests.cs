// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Text;
using Bhengu.Finance.Payments.BricsPay.Configuration;
using Bhengu.Finance.Payments.BricsPay.Currency;
using Bhengu.Finance.Payments.BricsPay.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.BricsPay;

public class BricsPayPaymentProviderTests
{
    private static BricsPayPaymentProvider CreateProvider(StubHandler handler, Mock<ICurrencyExchangeService>? exchangeMock = null)
    {
        var options = Options.Create(new BricsPayOptions
        {
            MerchantId = "BRICS_TEST",
            SecretKey = "secret",
            WebhookSecret = "webhook-secret",
            UseSandbox = true
        });
        var http = new HttpClient(handler);
        var exchange = exchangeMock ?? new Mock<ICurrencyExchangeService>();
        return new BricsPayPaymentProvider(http, options, exchange.Object, NullLogger<BricsPayPaymentProvider>.Instance);
    }

    [Fact]
    public void Constructor_ThrowsWhenMerchantIdMissing()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var ex = Assert.Throws<ProviderConfigurationException>(() =>
            new BricsPayPaymentProvider(
                new HttpClient(handler),
                Options.Create(new BricsPayOptions { SecretKey = "x" }),
                new Mock<ICurrencyExchangeService>().Object,
                NullLogger<BricsPayPaymentProvider>.Instance));
        Assert.Contains("MerchantId", ex.Message);
    }

    [Fact]
    public void Constructor_ThrowsWhenSecretKeyMissing()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        Assert.Throws<ProviderConfigurationException>(() =>
            new BricsPayPaymentProvider(
                new HttpClient(handler),
                Options.Create(new BricsPayOptions { MerchantId = "x" }),
                new Mock<ICurrencyExchangeService>().Object,
                NullLogger<BricsPayPaymentProvider>.Instance));
    }

    [Fact]
    public async Task ProcessPaymentAsync_SameCurrency_DoesNotInvokeExchange()
    {
        var exchange = new Mock<ICurrencyExchangeService>();
        var handler = new StubHandler((_, _) =>
            JsonResponse(HttpStatusCode.OK, """
                {"PaymentId":"BRICS-123","Status":"completed","Message":"ok"}
                """));

        var provider = CreateProvider(handler, exchange);
        var response = await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "token-1",
            Amount = 100m,
            Currency = "ZAR",
            Description = "test"
        });

        Assert.Equal("BRICS-123", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Equal("ZAR", response.Currency);
        exchange.Verify(e => e.LockRateAsync(It.IsAny<decimal>(), It.IsAny<BricsCurrency>(), It.IsAny<BricsCurrency>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VerifyWebhookSignature_TamperedPayload_ReturnsFalse()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var provider = CreateProvider(handler);

        var payload = """{"PaymentId":"x","Status":"completed"}""";
        Assert.False(provider.VerifyWebhookSignature(payload, "wrong-signature"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ValidPayload_ReturnsNormalisedEvent()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var provider = CreateProvider(handler);

        var payload = """
            {"EventType":"payment.completed","PaymentId":"BRICS-99","Status":"completed","Amount":100,"Currency":"ZAR"}
            """;
        var evt = await provider.ParseWebhookAsync(payload);

        Assert.NotNull(evt);
        Assert.Equal("BRICS-99", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("payment.completed", evt.EventType);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;
        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_handler(request, ct));
    }
}
