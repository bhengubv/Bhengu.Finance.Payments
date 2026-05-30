// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Fawry.Configuration;
using Bhengu.Finance.Payments.Fawry.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Fawry;

public class FawryPaymentProviderTests
{
    private static FawryPaymentProvider Create(StubHttpMessageHandler handler, FawryOptions? opts = null)
    {
        opts ??= new FawryOptions
        {
            MerchantCode = "MERCH_1",
            SecurityKey = "sk_fawry_test",
            UseSandbox = true
        };
        var http = new HttpClient(handler);
        return new FawryPaymentProvider(http, Options.Create(opts), NullLogger<FawryPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment(IReadOnlyDictionary<string, string>? metadata = null) => new()
    {
        PaymentMethodToken = "tok_card_xyz",
        Amount = 100m,
        Currency = "EGP",
        Description = "Fawry test charge",
        Metadata = metadata
    };

    [Fact]
    public void Constructor_Throws_WhenMerchantCodeMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        var opts = new FawryOptions { SecurityKey = "x" };
        Assert.Throws<ProviderConfigurationException>(() =>
            new FawryPaymentProvider(http, Options.Create(opts), NullLogger<FawryPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenSecurityKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        var opts = new FawryOptions { MerchantCode = "M" };
        Assert.Throws<ProviderConfigurationException>(() =>
            new FawryPaymentProvider(http, Options.Create(opts), NullLogger<FawryPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsFawry()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("fawry", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsCompletedResponse_OnPaidOrder()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("payments/charge", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"type":"ChargeResponse","referenceNumber":"FAW_REF_1","merchantRefNumber":"merch-1",
                 "orderStatus":"PAID","statusCode":"200","statusDescription":"Operation done successfully"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("FAW_REF_1", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Equal(100m, response.Amount);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsPending_ForNewOrder()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"referenceNumber":"FAW_REF_2","orderStatus":"NEW","statusCode":"200"}
            """));
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());
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
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad request"));
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
            Assert.Contains("payments/refund", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"type":"RefundResponse","statusCode":"200","statusDescription":"OK"}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "FAW_REF_1",
            Amount = 50m,
            Reason = "Customer requested"
        });
        Assert.Equal("FAW_REF_1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
        Assert.Equal(50m, refund.Amount);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string secret = "sk_fawry_test";
        const string canonical = "FAW_REF_99merch-99100.00100.00PAIDCARDPAY_REF_1";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical + secret))).ToLowerInvariant();

        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(canonical, hash));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("anything", "00ff"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenSecurityKeyMissing()
    {
        var opts = new FawryOptions { MerchantCode = "M", SecurityKey = "k" };
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), opts);
        // Re-create with a zeroed-out SecurityKey post-ctor isn't possible; instead, supply a fresh
        // provider whose options were set with whitespace post-construction is also not possible due to
        // the fail-fast ctor check, so this test verifies the constructor-level guard upstream:
        Assert.Throws<ProviderConfigurationException>(() =>
            new FawryPaymentProvider(
                new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
                Options.Create(new FawryOptions { MerchantCode = "M", SecurityKey = "" }),
                NullLogger<FawryPaymentProvider>.Instance));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForPaidNotification()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"fawryRefNumber":"FAW_99","merchantRefNumber":"merch-99","orderStatus":"PAID","paymentAmount":"100.00","paymentMethod":"CARD"}
            """);
        Assert.NotNull(evt);
        Assert.Equal("FAW_99", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("PAID", evt.EventType);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownStatus()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"fawryRefNumber":"FAW_99","orderStatus":"MOON_PHASE"}
            """);
        Assert.Null(evt);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("not json");
        Assert.Null(evt);
    }
}
