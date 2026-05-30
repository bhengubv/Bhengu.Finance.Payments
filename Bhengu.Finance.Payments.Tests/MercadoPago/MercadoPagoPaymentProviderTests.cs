// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.MercadoPago.Configuration;
using Bhengu.Finance.Payments.MercadoPago.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.MercadoPago;

public class MercadoPagoPaymentProviderTests
{
    private static MercadoPagoPaymentProvider Create(StubHttpMessageHandler handler, MercadoPagoOptions? opts = null)
    {
        opts ??= new MercadoPagoOptions
        {
            AccessToken = "TEST-1234567890",
            WebhookSecret = "webhook-test-secret"
        };
        var http = new HttpClient(handler);
        return new MercadoPagoPaymentProvider(http, Options.Create(opts), NullLogger<MercadoPagoPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment(Dictionary<string, string>? metadata = null) => new()
    {
        PaymentMethodToken = "tok_card_abc",
        Amount = 100m,
        Currency = "BRL",
        Description = "Mercado Pago test",
        Metadata = metadata ?? new Dictionary<string, string>
        {
            ["payment_method_id"] = "visa",
            ["payer_email"] = "buyer@example.com",
            ["identification_type"] = "CPF",
            ["identification_number"] = "12345678909"
        }
    };

    [Fact]
    public void Constructor_Throws_WhenAccessTokenMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new MercadoPagoPaymentProvider(http, Options.Create(new MercadoPagoOptions()), NullLogger<MercadoPagoPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsMercadoPago()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("mercadopago", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws_WhenPayerEmailMissing()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            provider.ProcessPaymentAsync(new PaymentRequest
            {
                PaymentMethodToken = "tok_x",
                Amount = 10m,
                Currency = "BRL",
                Description = "no-email"
            }));
        Assert.Equal("missing_payer_email", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsCompletedResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/v1/payments", req.RequestUri!.PathAndQuery);
            Assert.True(req.Headers.Contains("X-Idempotency-Key"));
            return StubHttpMessageHandler.Json(HttpStatusCode.Created, """
                {"id":12345,"status":"approved","status_detail":"accredited","transaction_amount":100.00,"currency_id":"BRL","payment_method_id":"visa"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("12345", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Equal(100m, response.Amount);
        Assert.Equal("BRL", response.Currency);
    }

    [Fact]
    public async Task ProcessPaymentAsync_HandlesPixWithoutCardToken()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            // PIX requests should NOT include a card token; ensure response handles the QR-bearing body.
            return StubHttpMessageHandler.Json(HttpStatusCode.Created, """
                {"id":67890,"status":"pending","status_detail":"pending_waiting_transfer","transaction_amount":100.00,"currency_id":"BRL","payment_method_id":"pix"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment(new Dictionary<string, string>
        {
            ["payment_method_id"] = "pix",
            ["payer_email"] = "buyer@example.com"
        }));

        Assert.Equal("67890", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate limited"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "invalid card"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS fail"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsRefundedResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("/v1/payments/12345/refunds", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.Created, """
                {"id":98765,"payment_id":12345,"amount":50.00,"status":"approved"}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "12345",
            Amount = 50m,
            Reason = "Customer requested"
        });

        Assert.Equal("98765", refund.GatewayReference);
        Assert.Equal(50m, refund.Amount);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("/v1/money_requests", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.Created, """
                {"id":55555,"status":"approved","amount":500.00,"currency_id":"BRL"}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "payee@example.com",
            Amount = 500m,
            Currency = "BRL",
            Description = "Vendor payout"
        });

        Assert.Equal("55555", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
        Assert.Equal(500m, payout.Amount);
        Assert.Equal("BRL", payout.Currency);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenWebhookSecretMissing()
    {
        var provider = Create(
            new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)),
            new MercadoPagoOptions { AccessToken = "TEST-x", WebhookSecret = "" });
        Assert.False(provider.VerifyWebhookSignature("manifest", "ts=1,v1=deadbeef"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string secret = "webhook-test-secret";
        const string manifest = "id:12345;request-id:abc-123;ts:1701390000;";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var validHash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        var header = $"ts=1701390000,v1={validHash}";

        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(manifest, header));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("id:1;request-id:r;ts:1;", "ts=1,v1=tampered00000000"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForPaymentApproved()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"type":"payment","action":"payment.approved","data":{"id":"99999"}}
            """);
        Assert.NotNull(evt);
        Assert.Equal("99999", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("payment.approved", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownEventType()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"type":"some.unknown","action":"who.knows","data":{"id":"x"}}
            """);
        Assert.Null(evt);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
