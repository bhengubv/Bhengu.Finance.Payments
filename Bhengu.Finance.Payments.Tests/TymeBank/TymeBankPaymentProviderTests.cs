// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.TymeBank.Configuration;
using Bhengu.Finance.Payments.TymeBank.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.TymeBank;

public class TymeBankPaymentProviderTests
{
    private static TymeBankPaymentProvider Create(StubHttpMessageHandler handler, TymeBankOptions? opts = null)
    {
        opts ??= new TymeBankOptions
        {
            ClientId = "tyme-client",
            ClientSecret = "tyme-secret",
            MerchantId = "MERCH-001",
            WebhookSecret = "webhook-tyme-secret",
            Currency = "ZAR",
            CallbackUrl = "https://example.com/tyme-callback"
        };
        var http = new HttpClient(handler);
        return new TymeBankPaymentProvider(http, Options.Create(opts), NullLogger<TymeBankPaymentProvider>.Instance);
    }

    private static StubHttpMessageHandler HandlerWithTokenAnd(
        Func<HttpRequestMessage, HttpResponseMessage> apiHandler) =>
        new((req, _) => req.RequestUri!.PathAndQuery.Contains("oauth2/token", StringComparison.OrdinalIgnoreCase)
            ? StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"access_token":"tyme-tok-123","token_type":"Bearer","expires_in":3600}
                """)
            : apiHandler(req));

    private static PaymentRequest SamplePayment(IReadOnlyDictionary<string, string>? metadata = null) => new()
    {
        PaymentMethodToken = "pay-ref-1",
        Amount = 250m,
        Currency = "ZAR",
        Description = "TymeBank test",
        Metadata = metadata ?? new Dictionary<string, string>
        {
            ["debtor_account"] = "1234567890",
            ["debtor_branch_code"] = "678910",
            ["creditor_account"] = "0987654321",
            ["creditor_branch_code"] = "123456",
            ["creditor_name"] = "Merchant Bhengu"
        }
    };

    [Fact]
    public void Constructor_Throws_WhenClientIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new TymeBankPaymentProvider(http, Options.Create(new TymeBankOptions { ClientSecret = "s" }),
                NullLogger<TymeBankPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenClientSecretMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new TymeBankPaymentProvider(http, Options.Create(new TymeBankOptions { ClientId = "c" }),
                NullLogger<TymeBankPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsTymeBank()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("tymebank", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsCompletedResponse_OnInstantSuccess()
    {
        var handler = HandlerWithTokenAnd(req =>
        {
            Assert.Contains("v1/payments/instant", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"payment_id":"pay_tyme_1","status":"completed","reference":"pay-ref-1"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("pay_tyme_1", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Equal(250m, response.Amount);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsPendingQrResponse_WhenModeIsQr()
    {
        var handler = HandlerWithTokenAnd(req =>
        {
            Assert.Contains("v1/qr/generate", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"qr_id":"qr_001","qr_string":"00020101...","qr_image_url":"https://qr/img"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment(new Dictionary<string, string>
        {
            ["mode"] = "qr",
            ["expiry_minutes"] = "5"
        }));

        Assert.Equal("qr_001", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal("00020101...", response.Message);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = HandlerWithTokenAnd(_ => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rl"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = HandlerWithTokenAnd(_ => StubHttpMessageHandler.Text(HttpStatusCode.UnprocessableEntity, "bad"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = HandlerWithTokenAnd(_ => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("net"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsRefundedResponse_OnSuccess()
    {
        var handler = HandlerWithTokenAnd(req =>
        {
            Assert.Contains("/refund", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"refund_id":"rf_tyme_1","status":"refunded"}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "pay_tyme_1",
            Amount = 100m,
            Reason = "Customer asked"
        });

        Assert.Equal("rf_tyme_1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = HandlerWithTokenAnd(req =>
        {
            Assert.Contains("v1/payouts", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"payout_id":"po_tyme_1","status":"completed"}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "678910:0123456789:Recipient Name",
            Amount = 500m,
            Currency = "ZAR",
            Description = "Supplier"
        });

        Assert.Equal("po_tyme_1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string secret = "webhook-tyme-secret";
        const string payload = """{"event_type":"payment.completed","data":{"payment_id":"p1"}}""";
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
    public void VerifyWebhookSignature_ReturnsFalse_WhenWebhookSecretMissing()
    {
        var provider = Create(
            new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)),
            new TymeBankOptions { ClientId = "c", ClientSecret = "s", WebhookSecret = "" });
        Assert.False(provider.VerifyWebhookSignature("payload", "sig"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForPaymentCompleted()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event_type":"payment.completed","data":{"payment_id":"pay_55","status":"completed"}}
            """);
        Assert.NotNull(evt);
        Assert.Equal("pay_55", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownEventType()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("""
            {"event_type":"unknown.event","data":{"payment_id":"x"}}
            """));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
