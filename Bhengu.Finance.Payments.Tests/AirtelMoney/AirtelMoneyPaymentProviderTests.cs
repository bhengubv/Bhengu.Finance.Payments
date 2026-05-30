// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.AirtelMoney.Configuration;
using Bhengu.Finance.Payments.AirtelMoney.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.AirtelMoney;

public class AirtelMoneyPaymentProviderTests
{
    private static AirtelMoneyOptions DefaultOptions() => new()
    {
        ClientId = "client-id",
        ClientSecret = "client-secret",
        Country = "KE",
        Currency = "KES",
        CallbackUrl = "https://example.com/airtel/cb",
        WebhookSecret = "airtel-webhook-secret",
        UseSandbox = true
    };

    private static AirtelMoneyPaymentProvider Create(StubHttpMessageHandler handler, AirtelMoneyOptions? opts = null)
    {
        opts ??= DefaultOptions();
        var http = new HttpClient(handler);
        return new AirtelMoneyPaymentProvider(http, Options.Create(opts), NullLogger<AirtelMoneyPaymentProvider>.Instance);
    }

    private static StubHttpMessageHandler OAuthAware(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> operationHandler) =>
        new((req, ct) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("auth/oauth2/token", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"access_token":"airtel-test-token","token_type":"Bearer","expires_in":3599}
                    """);
            return operationHandler(req, ct);
        });

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "254712345678",
        Amount = 1000m,
        Currency = "KES",
        Description = "Airtel test"
    };

    [Fact]
    public void Constructor_Throws_WhenClientIdMissing()
    {
        var opts = DefaultOptions();
        opts.ClientId = string.Empty;
        Assert.Throws<ProviderConfigurationException>(() => Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), opts));
    }

    [Fact]
    public void Constructor_Throws_WhenClientSecretMissing()
    {
        var opts = DefaultOptions();
        opts.ClientSecret = string.Empty;
        Assert.Throws<ProviderConfigurationException>(() => Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), opts));
    }

    [Fact]
    public void ProviderName_IsAirtelMoney()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Equal("airtelmoney", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsCompletedResponse_OnSuccess()
    {
        var handler = OAuthAware((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("merchant/v1/payments", req.RequestUri!.PathAndQuery);
            Assert.True(req.Headers.Contains("X-Country"));
            Assert.True(req.Headers.Contains("X-Currency"));
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":{"transaction":{"id":"abcdef12345","airtel_money_id":"AM-12345","status":"TS","message":"OK"}},"status":{"code":"200","message":"SUCCESS","success":true}}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("abcdef12345", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Equal("KES", response.Currency);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = OAuthAware((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "limit"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = OAuthAware((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = OAuthAware((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = OAuthAware((_, _) => throw new HttpRequestException("DNS fail"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsResponse_OnSuccess()
    {
        var handler = OAuthAware((req, _) =>
        {
            Assert.Contains("refund", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":{"transaction":{"airtel_money_id":"AM-RF-1","status":"TS"}},"status":{"success":true,"message":"refund accepted"}}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "AM-12345",
            Amount = 250m,
            Reason = "Customer requested"
        });

        Assert.Equal("AM-RF-1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, refund.Status);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = OAuthAware((req, _) =>
        {
            Assert.Contains("disbursements", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":{"transaction":{"id":"DISB-1","status":"TS"}},"status":{"success":true}}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "254712345678",
            Amount = 500m,
            Currency = "KES",
            Description = "Salary"
        });

        Assert.Equal("DISB-1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string secret = "airtel-webhook-secret";
        const string payload = """{"transaction":{"id":"x"}}""";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var validSig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));

        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(payload, validSig));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("payload", "tampered=="));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenSecretMissing()
    {
        var opts = DefaultOptions();
        opts.WebhookSecret = string.Empty;
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), opts);
        Assert.False(provider.VerifyWebhookSignature("payload", "sig"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ValidPayload_ReturnsNormalisedEvent()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"event_type":"payment.completed","transaction":{"id":"abc","airtel_money_id":"AM-99","status_code":"TS","message":"Success"}}
            """);

        Assert.NotNull(evt);
        Assert.Equal("AM-99", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("payment.completed", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
