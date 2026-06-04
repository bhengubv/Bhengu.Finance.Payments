// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.CMI.Configuration;
using Bhengu.Finance.Payments.CMI.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.CMI;

public class CMIPaymentProviderTests
{
    private static CMIPaymentProvider Create(StubHttpMessageHandler handler, CMIOptions? opts = null)
    {
        opts ??= new CMIOptions
        {
            ClientId = "600000001",
            StoreKey = "cmi_storekey_test",
            ApiUser = "api_user",
            ApiPassword = "api_password",
            OkUrl = "https://merchant.example/ok",
            FailUrl = "https://merchant.example/fail",
            CallbackUrl = "https://merchant.example/callback",
            Currency = "504",
            Lang = "en",
            UseSandbox = true
        };
        var http = new HttpClient(handler);
        return new CMIPaymentProvider(http, Options.Create(opts), NullLogger<CMIPaymentProvider>.Instance);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "ORDER_42",
        Amount = 150m,
        Currency = "MAD",
        Description = "CMI test",
        Metadata = new Dictionary<string, string>
        {
            ["email"] = "buyer@example.com",
            ["BillToName"] = "Ada Lovelace",
            ["rnd"] = "abc123"
        }
    };

    [Fact]
    public void Constructor_Throws_WhenClientIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ProviderConfigurationException>(() =>
            new CMIPaymentProvider(http, Options.Create(new CMIOptions { StoreKey = "x" }), NullLogger<CMIPaymentProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenStoreKeyMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ProviderConfigurationException>(() =>
            new CMIPaymentProvider(http, Options.Create(new CMIOptions { ClientId = "x" }), NullLogger<CMIPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsCmi()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Equal("cmi", provider.ProviderName);
    }

    [Fact]
    public void Capabilities_Include3DSAndTypedWebhooksAndSettlement()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.ThreeDSecure));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.TypedWebhooks));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Settlement));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.PartialRefund));
        Assert.False(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Tokenisation));
        Assert.False(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Payout));
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsPendingWithRedirectUrl_AndSignedHash()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("ORDER_42", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal(150m, response.Amount);
        Assert.NotNull(response.RedirectUrl);
        Assert.Contains("/fim/est3Dgate?", response.RedirectUrl);
        Assert.Contains("clientid=600000001", response.RedirectUrl);
        Assert.Contains("oid=ORDER_42", response.RedirectUrl);
        Assert.Contains("hash=", response.RedirectUrl);
        Assert.Contains("currency=504", response.RedirectUrl);
    }

    [Fact]
    public async Task ProcessPaymentAsync_GeneratesOid_WhenTokenMissing()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var response = await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "",
            Amount = 50m,
            Currency = "MAD",
            Description = "auto-oid"
        });
        Assert.StartsWith("cmi-", response.GatewayReference);
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsRefundedResponse_OnApprovedXml()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("fim/api", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Text(HttpStatusCode.OK,
                "<CC5Response><OrderId>ORDER_42</OrderId><Response>Approved</Response><ProcReturnCode>00</ProcReturnCode></CC5Response>");
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "ORDER_42",
            Amount = 75m,
            Reason = "Customer requested"
        });
        Assert.Equal("ORDER_42", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
        Assert.Equal(75m, refund.Amount);
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsFailed_OnDeclinedXml()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.OK,
            "<CC5Response><OrderId>X</OrderId><Response>Declined</Response><ProcReturnCode>05</ProcReturnCode></CC5Response>"));
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "X",
            Amount = 10m,
            Reason = "test"
        });
        Assert.Equal(PaymentStatus.Failed, refund.Status);
    }

    [Fact]
    public async Task ProcessRefundAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "X", Amount = 1m, Reason = "r"
        }));
    }

    [Fact]
    public async Task ProcessRefundAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "X", Amount = 1m, Reason = "r"
        }));
    }

    [Fact]
    public async Task ProcessRefundAsync_Throws5xxAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "X", Amount = 1m, Reason = "r"
        }));
    }

    [Fact]
    public async Task ProcessRefundAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("DNS"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "X", Amount = 1m, Reason = "r"
        }));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidHash()
    {
        const string storeKey = "cmi_storekey_test";
        const string canonical = "amount=150.00&clientid=600000001&oid=ORDER_42";
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(canonical + storeKey));
        var hash = Convert.ToBase64String(bytes);

        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature(canonical, hash));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedHash()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("anything", "AAAA"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsTypedChargeSucceeded_OnApprovedCallback()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("oid=ORDER_42&Response=Approved&ProcReturnCode=00&mdStatus=1&amount=150.00&currency=504");
        Assert.NotNull(evt);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal("ORDER_42", typed.GatewayReference);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(150m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsTypedChargeFailed_OnDeclinedCallback()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("oid=ORDER_42&Response=Declined&ProcReturnCode=05&amount=10.00&currency=504");
        Assert.NotNull(evt);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal("05", typed.FailureCode);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsTypedRefund_OnCreditCallback()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("oid=ORDER_42&TranType=Credit&Response=Approved&ProcReturnCode=00&amount=50.00&currency=504");
        Assert.NotNull(evt);
        var typed = Assert.IsType<RefundSucceededEvent>(evt);
        Assert.Equal(PaymentStatus.Refunded, typed.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_WhenOidMissing()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("Response=Approved&ProcReturnCode=00");
        Assert.Null(evt);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_WhenStatusUnknown()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("oid=ORDER_42&Response=Whatever&ProcReturnCode=zzz");
        Assert.Null(evt);
    }
}
