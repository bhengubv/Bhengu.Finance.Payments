// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.BricsPay.Configuration;
using Bhengu.Finance.Payments.BricsPay.Currency;
using Bhengu.Finance.Payments.BricsPay.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.BricsPay;

public class BricsPayPaymentProviderTests
{
    private static BricsPayPaymentProvider CreateProvider(StubHttpMessageHandler handler, Mock<ICurrencyExchangeService>? exchangeMock = null, BricsPayOptions? opts = null)
    {
        opts ??= new BricsPayOptions
        {
            MerchantId = "BRICS_TEST",
            SecretKey = "secret",
            WebhookSecret = "webhook-secret",
            UseSandbox = true
        };
        var http = new HttpClient(handler);
        var exchange = exchangeMock ?? new Mock<ICurrencyExchangeService>();
        return new BricsPayPaymentProvider(http, Options.Create(opts), exchange.Object, NullLogger<BricsPayPaymentProvider>.Instance);
    }

    [Fact]
    public void Constructor_Throws_WhenMerchantIdMissing()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var ex = Assert.Throws<ProviderConfigurationException>(() =>
            new BricsPayPaymentProvider(
                new HttpClient(handler),
                Options.Create(new BricsPayOptions { SecretKey = "x" }),
                new Mock<ICurrencyExchangeService>().Object,
                NullLogger<BricsPayPaymentProvider>.Instance));
        Assert.Contains("MerchantId", ex.Message);
    }

    [Fact]
    public void Constructor_Throws_WhenSecretKeyMissing()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
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
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
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
    public async Task ProcessPaymentAsync_CrossCurrency_LocksRateAndUsesConvertedAmount()
    {
        var exchange = new Mock<ICurrencyExchangeService>();
        exchange.Setup(e => e.LockRateAsync(100m, BricsCurrency.ZAR, BricsCurrency.INR, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversionResult
            {
                OriginalAmount = 100m,
                OriginalCurrency = BricsCurrency.ZAR,
                TargetCurrency = BricsCurrency.INR,
                ExchangeRate = 4.5m,
                FinalAmount = 450m,
                Fee = 0m,
                QuoteId = "QID-1",
                QuoteExpiry = DateTime.UtcNow.AddMinutes(15)
            });

        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"PaymentId":"BRICS-X","Status":"completed"}
                """));

        var provider = CreateProvider(handler, exchange);
        var response = await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "tok",
            Amount = 100m,
            Currency = "ZAR",
            Description = "cross-currency test",
            Metadata = new Dictionary<string, string> { ["target_currency"] = "INR" }
        });

        Assert.Equal("INR", response.Currency);
        Assert.Equal(450m, response.Amount);
        exchange.Verify(e => e.LockRateAsync(100m, BricsCurrency.ZAR, BricsCurrency.INR, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate limited"));
        var provider = CreateProvider(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "x",
            Amount = 1m,
            Currency = "ZAR",
            Description = "d"
        }));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "declined"));
        var provider = CreateProvider(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "x", Amount = 1m, Currency = "ZAR", Description = "d"
        }));
    }

    [Fact]
    public async Task ProcessPaymentAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("network down"));
        var provider = CreateProvider(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "x", Amount = 1m, Currency = "ZAR", Description = "d"
        }));
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("refunds", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"PaymentId":"BRICS-RF-1","Status":"refunded"}
                """);
        });
        var provider = CreateProvider(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "BRICS-123", Amount = 50m, Reason = "Customer requested"
        });
        Assert.Equal("BRICS-RF-1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("payouts", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"PaymentId":"BRICS-PO-1","Status":"completed"}
                """);
        });
        var provider = CreateProvider(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "dest-1", Amount = 200m, Currency = "ZAR", Description = "Payout to merchant"
        });
        Assert.Equal("BRICS-PO-1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string secret = "webhook-secret";
        const string payload = """{"EventType":"payment.completed","PaymentId":"x"}""";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var validSig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
        var provider = CreateProvider(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(payload, validSig));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = CreateProvider(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("anything", "tampered=="));
    }

    [Fact]
    public async Task ParseWebhookAsync_ValidPayload_ReturnsNormalisedEvent()
    {
        var provider = CreateProvider(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"EventType":"payment.completed","PaymentId":"BRICS-99","Status":"completed","Amount":100,"Currency":"ZAR"}
            """);

        Assert.NotNull(evt);
        Assert.Equal("BRICS-99", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("payment.completed", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = CreateProvider(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
