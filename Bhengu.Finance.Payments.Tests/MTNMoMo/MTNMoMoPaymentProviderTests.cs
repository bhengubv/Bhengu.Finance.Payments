// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.MTNMoMo.Configuration;
using Bhengu.Finance.Payments.MTNMoMo.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.MTNMoMo;

public class MTNMoMoPaymentProviderTests
{
    private static MTNMoMoOptions DefaultOptions() => new()
    {
        SubscriptionKey = "sub-key",
        ApiUserId = "00000000-0000-0000-0000-000000000001",
        ApiKey = "api-key-secret",
        TargetEnvironment = "sandbox",
        CallbackUrl = "https://example.com/momo/cb",
        UseSandbox = true
    };

    private static MTNMoMoPaymentProvider Create(StubHttpMessageHandler handler, MTNMoMoOptions? opts = null)
    {
        opts ??= DefaultOptions();
        var http = new HttpClient(handler);
        return new MTNMoMoPaymentProvider(http, Options.Create(opts), NullLogger<MTNMoMoPaymentProvider>.Instance);
    }

    private static StubHttpMessageHandler OAuthAware(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> operationHandler) =>
        new((req, ct) =>
        {
            if (req.RequestUri!.PathAndQuery.EndsWith("/token/", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"access_token":"momo-test-token","token_type":"Bearer","expires_in":3599}
                    """);
            return operationHandler(req, ct);
        });

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "256776123456",
        Amount = 1500m,
        Currency = "EUR",
        Description = "MoMo test"
    };

    [Fact]
    public void Constructor_Throws_WhenSubscriptionKeyMissing()
    {
        var opts = DefaultOptions();
        opts.SubscriptionKey = string.Empty;
        Assert.Throws<ProviderConfigurationException>(() => Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), opts));
    }

    [Fact]
    public void Constructor_Throws_WhenApiUserIdMissing()
    {
        var opts = DefaultOptions();
        opts.ApiUserId = string.Empty;
        Assert.Throws<ProviderConfigurationException>(() => Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), opts));
    }

    [Fact]
    public void ProviderName_IsMtnMoMo()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Equal("mtnmomo", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsPendingResponse_OnAcceptedRequestToPay()
    {
        var handler = OAuthAware((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("collection/v1_0/requesttopay", req.RequestUri!.PathAndQuery);
            Assert.True(req.Headers.Contains("X-Reference-Id"));
            Assert.True(req.Headers.Contains("X-Target-Environment"));
            Assert.True(req.Headers.Contains("Ocp-Apim-Subscription-Key"));
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.False(string.IsNullOrEmpty(response.GatewayReference));
        Assert.True(Guid.TryParse(response.GatewayReference, out _), "GatewayReference should be a UUID");
        Assert.Equal(PaymentStatus.Pending, response.Status);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = OAuthAware((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "throttled"));
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
    public async Task ProcessRefundAsync_Throws_BecauseRefundNotSupported()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "ref",
            Amount = 10m,
            Reason = "test"
        }));
        Assert.Contains("no refund API", ex.Message);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsPendingResponse_OnAcceptedTransfer()
    {
        var handler = OAuthAware((req, _) =>
        {
            Assert.Contains("disbursement/v1_0/transfer", req.RequestUri!.PathAndQuery);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "256776123456",
            Amount = 500m,
            Currency = "EUR",
            Description = "Payout"
        });

        Assert.True(Guid.TryParse(payout.GatewayReference, out _));
        Assert.Equal(PaymentStatus.Pending, payout.Status);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_BecauseMoMoDoesNotSign()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("payload", "signature"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsCompletedEvent_ForSuccessfulStatus()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"financialTransactionId":"23503452","externalId":"ext-1","amount":"100","currency":"EUR","payer":{"partyIdType":"MSISDN","partyId":"256776123456"},"status":"SUCCESSFUL"}
            """);
        Assert.NotNull(evt);
        Assert.Equal("23503452", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("successful", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsFailedEvent_ForFailedStatus()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"financialTransactionId":"x","externalId":"ext-1","status":"FAILED","reason":"PAYER_NOT_FOUND"}
            """);
        Assert.NotNull(evt);
        Assert.Equal(PaymentStatus.Failed, evt!.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(OAuthAware((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
