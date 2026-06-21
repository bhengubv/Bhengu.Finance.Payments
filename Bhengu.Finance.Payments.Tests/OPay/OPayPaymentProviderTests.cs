// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.OPay.Configuration;
using Bhengu.Finance.Payments.OPay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.OPay;

public class OPayPaymentProviderTests
{
    private static OPayPaymentProvider Create(StubHttpMessageHandler handler, OPayOptions? opts = null)
    {
        opts ??= new OPayOptions
        {
            PublicKey = "opay-pub-key",
            SecretKey = "opay-secret-key",
            MerchantId = "MERCH123",
            Country = "NG",
            CallbackUrl = "https://example.com/callback",
            ReturnUrl = "https://example.com/return"
        };
        var http = new HttpClient(handler);
        return new OPayPaymentProvider(http, Options.Create(opts), NullLogger<OPayPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "CARD",
        Amount = 100m,
        Currency = "NGN",
        Description = "OPay test"
    };

    [Fact]
    public void Constructor_Throws_WhenPublicKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new OPayPaymentProvider(http,
                Options.Create(new OPayOptions { SecretKey = "s", MerchantId = "m" }),
                NullLogger<OPayPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenSecretKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new OPayPaymentProvider(http,
                Options.Create(new OPayOptions { PublicKey = "p", MerchantId = "m" }),
                NullLogger<OPayPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenMerchantIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new OPayPaymentProvider(http,
                Options.Create(new OPayOptions { PublicKey = "p", SecretKey = "s" }),
                NullLogger<OPayPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsOPay()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("opay", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsCompletedResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("cashier/create", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code":"00000","message":"OK","data":{"reference":"ref-1","orderNo":"OPAY-ORD-1","cashierUrl":"https://opay/p/1","status":"success"}}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("OPAY-ORD-1", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Equal(100m, response.Amount);
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
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad data"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.InternalServerError, "down"));
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
    public async Task ProcessRefundAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            // Verified path: https://documentation.opaycheckout.com/payment-refund
            Assert.Contains("payment/refund/create", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code":"00000","message":"OK","data":{"reference":"REFUND-1","originalReference":"OPAY-ORD-1","orderNo":"OPAY-RF-1","orderStatus":"SUCCESS"}}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "OPAY-ORD-1",
            Amount = 50m,
            Reason = "Customer requested"
        });

        Assert.Equal("OPAY-RF-1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, refund.Status);
        Assert.Equal(50m, refund.Amount);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("payout/create", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code":"00000","message":"OK","data":{"orderNo":"OPAY-PO-1","status":"success"}}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "USR-9000",
            Amount = 500m,
            Currency = "NGN",
            Description = "Vendor payout"
        });

        Assert.Equal("OPAY-PO-1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
    }

    // OPay callbacks carry an HMAC-SHA3-512 (hex) `sha512` over a fixed sign-content string built
    // from 8 payload fields, keyed on the merchant Private Key (SecretKey).
    // Source: https://documentation.opaycheckout.com/callback-signature
    private const string CallbackSecret = "opay-secret-key";

    // A real OPay payment-notification callback body. `amount` is a scalar (minor units) with a
    // sibling `currency`; `refunded` is a bool; `status` is SUCCESS/FAIL/... ; the signed fields are
    // Amount, Currency, Reference, Refunded, Status, Timestamp, Token, TransactionID.
    private const string CallbackBody =
        """{"payload":{"amount":250000,"currency":"NGN","reference":"OPAY-1","refunded":false,"status":"SUCCESS","timestamp":"2026-06-21T10:00:00Z","token":"TOK-1","transactionId":"TX-1","instrumentType":"BankCard"},"type":"transaction-status"}""";

    /// <summary>Compute the exact `sha512` value OPay would send for <see cref="CallbackBody"/>.</summary>
    private static string ValidCallbackSha512()
    {
        // {Amount:"%s",Currency:"%s",Reference:"%s",Refunded:%s,Status:"%s",Timestamp:"%s",Token:"%s",TransactionID:"%s"}
        var signContent =
            "{Amount:\"250000\",Currency:\"NGN\",Reference:\"OPAY-1\",Refunded:f,Status:\"SUCCESS\",Timestamp:\"2026-06-21T10:00:00Z\",Token:\"TOK-1\",TransactionID:\"TX-1\"}";
        var mac = HMACSHA3_512.HashData(Encoding.UTF8.GetBytes(CallbackSecret), Encoding.UTF8.GetBytes(signContent));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));

        if (!HMACSHA3_512.IsSupported)
        {
            // No SHA-3 on this runtime: the verifier degrades safely to false (never throws / passes
            // blindly). We cannot compute a valid signature here, so assert the safe-reject contract.
            Assert.False(provider.VerifyWebhookSignature(CallbackBody, new string('a', 128)));
            return;
        }

        Assert.True(provider.VerifyWebhookSignature(CallbackBody, ValidCallbackSha512()));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_WhenSignatureReadFromBody()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));

        if (!HMACSHA3_512.IsSupported)
        {
            Assert.False(provider.VerifyWebhookSignature(CallbackBody, ""));
            return;
        }

        // Embed the sha512 in the body and call with an empty signature arg — the verifier reads it.
        var body = CallbackBody.Replace(
            "\"type\":\"transaction-status\"",
            $"\"sha512\":\"{ValidCallbackSha512()}\",\"type\":\"transaction-status\"",
            StringComparison.Ordinal);
        Assert.True(provider.VerifyWebhookSignature(body, ""));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        // A wrong signature must never verify (true regardless of SHA-3 availability: no-support → false).
        Assert.False(provider.VerifyWebhookSignature(CallbackBody, new string('a', 128)));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForMalformedBody()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("not json", "deadbeef"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForTransactionSuccess()
    {
        // Real OPay callback: single envelope type "transaction-status", outcome in payload.status.
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"type":"transaction-status","payload":{"reference":"OPAY-99","status":"SUCCESS","amount":250000,"currency":"NGN"}}
            """);
        Assert.NotNull(evt);
        Assert.Equal("OPAY-99", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("transaction-status", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsPending_ForInitialStatus()
    {
        // OPay's only envelope type is "transaction-status"; an INITIAL/PENDING status is surfaced as
        // a pending charge (there is no separate "unknown event type" concept in the real callback).
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"type":"transaction-status","payload":{"reference":"X","status":"INITIAL"}}
            """);
        Assert.NotNull(evt);
        Assert.Equal(Bhengu.Finance.Payments.Core.Models.Webhooks.WebhookEventCategory.ChargePending, evt!.Category);
        Assert.Equal(PaymentStatus.Pending, evt.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
