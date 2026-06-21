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
            ApiKey = "apikey-test",
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
    public async Task ProcessPaymentAsync_ReturnsPendingWithHostedUrl_OnSuccess()
    {
        // Verified v3 express-request: path /v3/checkout-api/checkout-request/express-request,
        // body in snake_case carrying the apiKey + Bearer; response is a status/results envelope.
        // Source: https://docs.tingg.africa/docs/checkout-v3-express-checkout
        var handler = ComposeWithToken((req, _) =>
        {
            Assert.Contains("v3/checkout-api/checkout-request/express-request", req.RequestUri!.PathAndQuery);
            Assert.True(req.Headers.Contains("apiKey"), "apiKey header must be present on checkout call");
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":{"status_code":200,"status_description":"success"},"results":{"short_url":"https://api.tingg.africa/checkout/abc123","long_url":"https://api.tingg.africa/checkout?access_key=k&encrypted_payload=p"}}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        // Express returns the hosted payment page; the payment itself is pending until completed.
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal("https://api.tingg.africa/checkout/abc123", response.RedirectUrl);
        Assert.False(string.IsNullOrEmpty(response.GatewayReference));
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
        // Verified host/path: POST {payout-host}/v1/global-api/payments (Tingg Payouts global-api).
        // Source: https://docs.tingg.africa/reference/postpayment . NOTE: request/response body is
        // UNVERIFIED in the provider; this asserts the host/path + status mapping only.
        var handler = ComposeWithToken((req, _) =>
        {
            Assert.Contains("v1/global-api/payments", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"authStatusCode":200,"authStatusDescription":"ok","results":[{"statusCode":139,"statusDescription":"Payment posted successfully and pending acknowledgement","payerTransactionID":"ptx-1","beepTransactionID":"beep-1"}]}
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

        Assert.Equal("beep-1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, payout.Status);
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsPending_OnSuccessfullyLoggedRefund()
    {
        // Verified refund: POST /v3/checkout-api/refund/request, body with refund_type/amount/
        // refund_reference; response is a status envelope (200 = request logged, async).
        // Source: https://docs.tingg.africa/reference/refund
        var handler = ComposeWithToken((req, _) =>
        {
            Assert.Contains("v3/checkout-api/refund/request", req.RequestUri!.PathAndQuery);
            Assert.True(req.Headers.Contains("apiKey"), "apiKey header must be present on refund call");
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":{"status_code":200,"status_description":"Refund request successfully logged"}}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "merchant-txn-1",
            Amount = 250m,
            Reason = "Customer requested"
        });

        Assert.False(string.IsNullOrEmpty(refund.GatewayReference));
        Assert.Equal(PaymentStatus.Pending, refund.Status);
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
        // NOTE: the HMAC scheme itself is UNVERIFIED against Tingg (their public callback docs
        // describe no signature). This test only proves the retained HMAC-SHA256 hex check is wired
        // through SignatureHelpers correctly when a WebhookSecret is configured.
        const string secret = "webhook-test-secret";
        const string payload = """{"request_status_code":178,"merchant_transaction_id":"merchant-txn-1"}""";
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
        // Verified IPN shape: snake_case, status-code driven (178 = full payment).
        // Source: https://docs.tingg.africa/reference/4-implement-webhook-via-callback-url-1
        var provider = Create(ComposeWithToken((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"request_status_code":178,"request_status_description":"Full payment made","merchant_transaction_id":"merchant-txn-1","checkout_request_id":"chk_99","amount_paid":250,"currency_code":"KES","MSISDN":"254712000000"}
            """);
        Assert.NotNull(evt);
        Assert.Equal("merchant-txn-1", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownStatusCode()
    {
        var provider = Create(ComposeWithToken((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("""
            {"request_status_code":130,"merchant_transaction_id":"x"}
            """));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(ComposeWithToken((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
