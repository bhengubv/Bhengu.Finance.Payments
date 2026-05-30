// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.PagSeguro.Configuration;
using Bhengu.Finance.Payments.PagSeguro.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PagSeguro;

public class PagSeguroPaymentProviderTests
{
    private static PagSeguroPaymentProvider Create(StubHttpMessageHandler handler, PagSeguroOptions? opts = null)
    {
        opts ??= new PagSeguroOptions
        {
            ApiToken = "pagbank-test-token",
            WebhookSecret = "webhook-test-secret"
        };
        var http = new HttpClient(handler);
        return new PagSeguroPaymentProvider(http, Options.Create(opts), NullLogger<PagSeguroPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment(Dictionary<string, string>? metadata = null) => new()
    {
        PaymentMethodToken = "ENCRYPTED_CARD_TOKEN_abc",
        Amount = 100m,
        Currency = "BRL",
        Description = "PagSeguro test",
        Metadata = metadata ?? new Dictionary<string, string>
        {
            ["payment_method_type"] = "CREDIT_CARD",
            ["customer_name"] = "Joao Silva",
            ["customer_email"] = "joao@example.com",
            ["customer_tax_id"] = "12345678909",
            ["holder_name"] = "JOAO SILVA"
        }
    };

    [Fact]
    public void Constructor_Throws_WhenApiTokenMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new PagSeguroPaymentProvider(http, Options.Create(new PagSeguroOptions()), NullLogger<PagSeguroPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsPagSeguro()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("pagseguro", provider.ProviderName);
    }

    [Fact]
    public void Constructor_UsesSandboxUrl_WhenUseSandboxTrue()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var http = new HttpClient(handler);
        var provider = new PagSeguroPaymentProvider(
            http,
            Options.Create(new PagSeguroOptions { ApiToken = "x", UseSandbox = true }),
            NullLogger<PagSeguroPaymentProvider>.Instance);
        Assert.NotNull(provider);
        Assert.Equal("https://sandbox.api.pagseguro.com/", http.BaseAddress!.ToString());
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsCompletedResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/orders", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.Created, """
                {"id":"ORDE_abc123","reference_id":"r1","status":"PAID","charges":[{"id":"CHAR_xyz","status":"PAID","amount":{"value":10000,"currency":"BRL"}}]}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("ORDE_abc123", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Equal(100m, response.Amount);
        Assert.Equal("BRL", response.Currency);
    }

    [Fact]
    public async Task ProcessPaymentAsync_HandlesPixCharge()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.Created, """
            {"id":"ORDE_pix1","reference_id":"r2","status":"WAITING","charges":[{"id":"CHAR_pix1","status":"WAITING","amount":{"value":10000,"currency":"BRL"}}]}
            """));
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment(new Dictionary<string, string>
        {
            ["payment_method_type"] = "PIX",
            ["customer_email"] = "joao@example.com"
        }));

        Assert.Equal("ORDE_pix1", response.GatewayReference);
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
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.UnprocessableEntity, "validation error"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.ServiceUnavailable, "down"));
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
            Assert.Contains("/charges/CHAR_xyz/cancel", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"CHAR_xyz","status":"CANCELED","amount":{"value":5000,"currency":"BRL"}}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "CHAR_xyz",
            Amount = 50m,
            Reason = "Customer requested"
        });

        Assert.Equal("CHAR_xyz", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Cancelled, refund.Status);
        Assert.Equal(50m, refund.Amount);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("/transfers", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.Created, """
                {"id":"TRAN_111","reference_id":"transfer-x","status":"PROCESSING","amount":{"value":50000,"currency":"BRL"}}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "0001|12345-6|7|12345678909|JOAO SILVA|001",
            Amount = 500m,
            Currency = "BRL",
            Description = "Vendor payout"
        });

        Assert.Equal("TRAN_111", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, payout.Status);
        Assert.Equal(500m, payout.Amount);
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws_WhenDestinationTokenMalformed()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "bad-token",
            Amount = 100m,
            Currency = "BRL",
            Description = "bad"
        }));
        Assert.Equal("invalid_destination_token", ex.ProviderErrorCode);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenWebhookSecretMissing()
    {
        var provider = Create(
            new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)),
            new PagSeguroOptions { ApiToken = "x", WebhookSecret = "" });
        Assert.False(provider.VerifyWebhookSignature("payload", "sig"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string secret = "webhook-test-secret";
        const string payload = """{"id":"ORDE_1","status":"PAID","charges":[{"id":"CHAR_1","status":"PAID"}]}""";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var validSig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(payload, validSig));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("anything", "deadbeef"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForOrderPaid()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"id":"ORDE_99","status":"PAID","charges":[{"id":"CHAR_99","status":"PAID","amount":{"value":10000,"currency":"BRL"}}]}
            """);
        Assert.NotNull(evt);
        Assert.Equal("ORDE_99", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("PAID", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownStatus()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"id":"ORDE_99","status":"SOME_NEW_STATUS","charges":[{"id":"CHAR_99","status":"SOME_NEW_STATUS"}]}
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
