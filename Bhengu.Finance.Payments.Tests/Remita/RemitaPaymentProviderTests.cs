// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Remita.Configuration;
using Bhengu.Finance.Payments.Remita.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Remita;

public class RemitaPaymentProviderTests
{
    private static RemitaPaymentProvider Create(StubHttpMessageHandler handler, RemitaOptions? opts = null)
    {
        opts ??= new RemitaOptions
        {
            MerchantId = "2547916",
            ServiceTypeId = "4430731",
            ApiKey = "1946",
            ApiToken = "tok-test",
            FromBank = "044",
            DebitAccount = "0690000031",
            Currency = "NGN",
            CallbackUrl = "https://example.com/remita-callback"
        };
        var http = new HttpClient(handler);
        return new RemitaPaymentProvider(http, Options.Create(opts), NullLogger<RemitaPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "ORDER-001",
        Amount = 1000m,
        Currency = "NGN",
        Description = "Remita test",
        Metadata = new Dictionary<string, string>
        {
            ["payerName"] = "Test Payer",
            ["payerEmail"] = "payer@example.com",
            ["payerPhone"] = "08012345678"
        }
    };

    private static string Sha512Hex(string input)
    {
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    [Fact]
    public void Constructor_Throws_WhenMerchantIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new RemitaPaymentProvider(http, Options.Create(new RemitaOptions
            {
                ServiceTypeId = "s",
                ApiKey = "k"
            }), NullLogger<RemitaPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenApiKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new RemitaPaymentProvider(http, Options.Create(new RemitaOptions
            {
                MerchantId = "m",
                ServiceTypeId = "s"
            }), NullLogger<RemitaPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsRemita()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("remita", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsPendingResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("paymentinit", req.RequestUri!.PathAndQuery);
            Assert.True(req.Headers.Contains("Authorization"));
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"statuscode":"025","RRR":"290008931930","status":"Payment Reference generated"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("290008931930", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal(1000m, response.Amount);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "throttled"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "invalid hash"));
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
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(SamplePayment()));
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("refund/initiate", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"statuscode":"0","status":"Refund Initiated","refundReference":"REF-12345"}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "290008931930",
            Amount = 500m,
            Reason = "Customer cancelled"
        });

        Assert.Equal("REF-12345", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, refund.Status);
        Assert.Equal(500m, refund.Amount);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("sendmoney", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"statuscode":"00","status":"Approved","transRef":"sm-xyz"}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "058:0123456789",
            Amount = 750m,
            Currency = "NGN",
            Description = "Vendor disbursement"
        });

        Assert.Equal("sm-xyz", payout.GatewayReference);
        Assert.Equal(750m, payout.Amount);
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws_OnMissingDestinationColon()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            provider.ProcessPayoutAsync(new PayoutRequest
            {
                DestinationToken = "no-colon-here",
                Amount = 100m,
                Currency = "NGN",
                Description = "x"
            }));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string apiKey = "1946";
        const string rrr = "290008931930";
        const string status = "00";
        var payload = $$"""{"rrr":"{{rrr}}","status":"{{status}}"}""";
        var expected = Sha512Hex(rrr + status + apiKey);

        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(payload, expected));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature(
            """{"rrr":"290008931930","status":"00"}""",
            "00deadbeef"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenRrrMissing()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature(
            """{"status":"00"}""",
            "anything"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForSuccessfulCallback()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"rrr":"290008931930","status":"00","orderId":"ORDER-001"}
            """);
        Assert.NotNull(evt);
        Assert.Equal("290008931930", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownStatus()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"rrr":"290008931930","status":"weird","orderId":"x"}
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
