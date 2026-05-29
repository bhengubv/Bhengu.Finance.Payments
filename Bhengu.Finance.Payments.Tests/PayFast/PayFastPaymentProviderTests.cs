// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Bhengu.Finance.Payments.PayFast.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayFast;

public class PayFastPaymentProviderTests
{
    private static PayFastPaymentProvider CreateProvider(StubHandler handler, PayFastOptions? opts = null)
    {
        opts ??= new PayFastOptions { MerchantId = "10000100", Passphrase = "jt7NOE43FZPn", UseSandbox = true };
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://sandbox.payfast.co.za/") };
        return new PayFastPaymentProvider(
            http,
            Options.Create(opts),
            NullLogger<PayFastPaymentProvider>.Instance);
    }

    private static PaymentRequest SampleRequest() => new()
    {
        PaymentMethodToken = "f4c8e1d2-1234-5678-9abc-def012345678",
        Amount = 99.99m,
        Currency = "ZAR",
        Description = "Test charge"
    };

    [Fact]
    public void Constructor_ThrowsProviderConfigurationException_WhenMerchantIdMissing()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var http = new HttpClient(handler);
        var opts = Options.Create(new PayFastOptions { Passphrase = "x" });

        var ex = Assert.Throws<ProviderConfigurationException>(() =>
            new PayFastPaymentProvider(http, opts, NullLogger<PayFastPaymentProvider>.Instance));

        Assert.Equal("payfast", ex.ProviderName);
        Assert.Contains("MerchantId", ex.Message);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsCompletedResponse_OnSuccess()
    {
        var handler = new StubHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("subscriptions/", req.RequestUri!.PathAndQuery);
            return JsonResponse(HttpStatusCode.OK, """
                {"code":200,"status":"success","data":{"message":true,"pf_payment_id":"PF-12345","response":"APPROVED","response_reason":"OK"}}
                """);
        });

        var provider = CreateProvider(handler);
        var response = await provider.ProcessPaymentAsync(SampleRequest());

        Assert.Equal("PF-12345", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Equal(99.99m, response.Amount);
        Assert.Equal("ZAR", response.Currency);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHandler((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("rate limited", Encoding.UTF8)
            };
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return resp;
        });

        var provider = CreateProvider(handler);
        var ex = await Assert.ThrowsAsync<ProviderRateLimitException>(() =>
            provider.ProcessPaymentAsync(SampleRequest()));

        Assert.Equal(30, ex.RetryAfterSeconds);
        Assert.Equal("payfast", ex.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("invalid card", Encoding.UTF8)
            });

        var provider = CreateProvider(handler);
        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            provider.ProcessPaymentAsync(SampleRequest()));

        Assert.Equal("400", ex.ProviderErrorCode);
        Assert.Contains("invalid card", ex.ProviderErrorMessage);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("upstream down", Encoding.UTF8)
            });

        var provider = CreateProvider(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            provider.ProcessPaymentAsync(SampleRequest()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("DNS failure"));

        var provider = CreateProvider(handler);
        var ex = await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            provider.ProcessPaymentAsync(SampleRequest()));

        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsManualTrackingReference()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var provider = CreateProvider(handler);

        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "PF-12345",
            Amount = 50m,
            Reason = "Customer requested"
        });

        Assert.StartsWith("PAYFAST-MANUAL-REFUND-PF-12345", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, refund.Status);
        Assert.Equal(50m, refund.Amount);
        Assert.Contains("manual", refund.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParseWebhookAsync_NormalisesPayFastItnToWebhookEvent()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var provider = CreateProvider(handler);

        var payload = "m_payment_id=tx-123&pf_payment_id=PF-99&payment_status=COMPLETE&amount_gross=99.99";
        var evt = await provider.ParseWebhookAsync(payload);

        Assert.NotNull(evt);
        Assert.Equal("PF-99", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("payfast.itn", evt.EventType);
        Assert.Equal("tx-123", evt.RawPayload!["m_payment_id"]);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalseForTamperedPayload()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var provider = CreateProvider(handler);

        var tampered = "m_payment_id=tx-123&payment_status=COMPLETE&signature=deadbeef";
        Assert.False(provider.VerifyWebhookSignature(tampered, "deadbeef"));
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
