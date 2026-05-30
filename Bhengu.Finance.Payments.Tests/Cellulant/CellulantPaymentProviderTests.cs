// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Cellulant.Configuration;
using Bhengu.Finance.Payments.Cellulant.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Cellulant;

public class CellulantPaymentProviderTests
{
    /// <summary>
    /// Cellulant requires an OAuth token before every authorised call. This helper composes a
    /// handler that fires the token response on the OAuth path and delegates everything else
    /// to the supplied <paramref name="businessHandler"/>.
    /// </summary>
    private static StubHttpMessageHandler ComposeWithToken(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> businessHandler) =>
        new((req, ct) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("oauth/token"))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"access_token":"tok_test","expires_in":3600,"token_type":"bearer"}
                    """);
            return businessHandler(req, ct);
        });

    private static CellulantPaymentProvider Create(StubHttpMessageHandler handler, CellulantOptions? opts = null)
    {
        opts ??= new CellulantOptions
        {
            ServiceCode = "TGNTEST",
            ClientId = "client-1",
            ClientSecret = "secret-1",
            WebhookSecret = "webhook-test-secret",
            CallbackUrl = "https://example.com/cb",
            UseSandbox = true
        };
        var http = new HttpClient(handler);
        return new CellulantPaymentProvider(http, Options.Create(opts), NullLogger<CellulantPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "254712000000",
        Amount = 250m,
        Currency = "KES",
        Description = "Cellulant test"
    };

    [Fact]
    public void Constructor_Throws_WhenServiceCodeMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new CellulantPaymentProvider(http, Options.Create(new CellulantOptions { ClientId = "c", ClientSecret = "s" }), NullLogger<CellulantPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsCellulant()
    {
        var provider = Create(ComposeWithToken((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("cellulant", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsCompletedResponse_OnSuccess()
    {
        var handler = ComposeWithToken((req, _) =>
        {
            Assert.Contains("checkout/v3/express", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"checkoutRequestId":"chk-tingg-1","status":"success","redirectUrl":"https://online.tingg.africa/checkout/chk-tingg-1"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("chk-tingg-1", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = ComposeWithToken((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate limited"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = ComposeWithToken((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "invalid msisdn"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = ComposeWithToken((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.InternalServerError, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = ComposeWithToken((_, _) => throw new HttpRequestException("DNS fail"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = ComposeWithToken((req, _) =>
        {
            Assert.Contains("disbursement/v1/initiate", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"transactionReference":"mula-1","status":"processing"}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "254712000000",
            Amount = 1000m,
            Currency = "KES",
            Description = "Vendor payout"
        });

        Assert.Equal("mula-1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, payout.Status);
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsRefundedResponse_OnSuccess()
    {
        var handler = ComposeWithToken((req, _) =>
        {
            Assert.Contains("refunds", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"refundReference":"rf-tingg-1","status":"processed"}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "chk-tingg-1",
            Amount = 250m,
            Reason = "Customer requested"
        });

        Assert.Equal("rf-tingg-1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, refund.Status);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenWebhookSecretMissing()
    {
        var provider = Create(
            ComposeWithToken((_, _) => new HttpResponseMessage(HttpStatusCode.OK)),
            new CellulantOptions { ServiceCode = "x", ClientId = "c", ClientSecret = "s", WebhookSecret = "" });
        Assert.False(provider.VerifyWebhookSignature("payload", "abcdef"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string secret = "webhook-test-secret";
        const string payload = """{"eventType":"payment.success","data":{"checkoutRequestId":"chk_1"}}""";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var validSig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        var provider = Create(ComposeWithToken((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(payload, validSig));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(ComposeWithToken((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("anything", "deadbeef"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForPaymentSuccess()
    {
        var provider = Create(ComposeWithToken((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"eventType":"payment.success","data":{"checkoutRequestId":"chk_99","status":"success"}}
            """);
        Assert.NotNull(evt);
        Assert.Equal("chk_99", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownEventType()
    {
        var provider = Create(ComposeWithToken((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("""
            {"eventType":"unknown","data":{"checkoutRequestId":"x"}}
            """));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(ComposeWithToken((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
