// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.DPO.Configuration;
using Bhengu.Finance.Payments.DPO.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.DPO;

public class DPOPaymentProviderTests
{
    private static DPOPaymentProvider Create(StubHttpMessageHandler handler, DPOOptions? opts = null, IBhenguDistributedCache? cache = null)
    {
        opts ??= new DPOOptions
        {
            CompanyToken = "DPO_TEST_COMPANY_TOKEN",
            ServiceType = "3854",
            ServiceDescription = "Online Test",
            RedirectUrl = "https://example.com/return",
            BackUrl = "https://example.com/back",
            UseSandbox = true
        };
        var http = new HttpClient(handler);
        return new DPOPaymentProvider(http, Options.Create(opts), NullLogger<DPOPaymentProvider>.Instance, cache);
    }

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "ORDER-1001",
        Amount = 150m,
        Currency = "USD",
        Description = "DPO test order"
    };

    [Fact]
    public void Constructor_Throws_WhenCompanyTokenMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new DPOPaymentProvider(http, Options.Create(new DPOOptions()), NullLogger<DPOPaymentProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsDpo()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("dpo", provider.ProviderName);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsPendingResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("api/v6/", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"Result":"000","ResultExplanation":"Transaction created","TransToken":"57466900-3636-4297-9D8D-32EF99F50DBD","TransRef":"100"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal("57466900-3636-4297-9D8D-32EF99F50DBD", response.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, response.Status);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ThrowsPaymentDeclined_OnNonZeroResultCode()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"Result":"904","ResultExplanation":"Invalid amount"}
            """));
        var provider = Create(handler);
        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(SamplePayment()));
        Assert.Equal("904", ex.ProviderErrorCode);
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
            Assert.Contains("api/v6/", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"Result":"000","ResultExplanation":"Refunded"}
                """);
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "57466900-3636-4297-9D8D-32EF99F50DBD",
            Amount = 150m,
            Reason = "Customer requested"
        });

        Assert.Equal(PaymentStatus.Refunded, refund.Status);
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsFailedStatus_OnNonZeroResult()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"Result":"904","ResultExplanation":"Already refunded"}
            """));
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "tt",
            Amount = 1m,
            Reason = "x"
        });
        Assert.Equal(PaymentStatus.Failed, refund.Status);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_WhenSignaturePresent()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(provider.VerifyWebhookSignature("payload", "any-non-empty"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_WhenSignatureMissing()
    {
        // DPO does not sign callbacks; an empty signature header is simply treated as
        // "no verification possible" and the method returns false rather than throwing.
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.False(provider.VerifyWebhookSignature("payload", ""));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsEvent_ForPaidStatus()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"TransID":"100","TransactionToken":"tt-99","CompanyRef":"ORDER-1001","TransactionFinalStatus":"Paid"}
            """);
        Assert.NotNull(evt);
        Assert.Equal("tt-99", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForUnknownStatus()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("""
            {"TransID":"x","TransactionToken":"x","TransactionFinalStatus":"WhateverElse"}
            """));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsTypedChargeSucceededEvent_ForPaid()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var evt = await provider.ParseWebhookAsync("""
            {"TransID":"100","TransactionToken":"tt-99","TransactionFinalStatus":"Paid","TransactionAmount":"150","TransactionCurrency":"USD","CustomerEmail":"a@b.com","CCDapproval":"ABC123"}
            """);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(150m, typed.Amount);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ReturnsPendingResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"Result":"000","TransferToken":"XF-1","ResultExplanation":"queued"}
                """);
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "1234567890",
            Amount = 250m,
            Currency = "USD",
            Description = "Vendor payout"
        });

        Assert.Equal("XF-1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Pending, payout.Status);
    }

    [Fact]
    public async Task ProcessPayoutAsync_ThrowsPaymentDeclined_OnNonZeroResultCode()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"Result":"802","ResultExplanation":"Insufficient funds"}
            """));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "1",
            Amount = 1m,
            Currency = "USD",
            Description = "x"
        }));
    }

    [Fact]
    public async Task ProcessPaymentAsync_DedupesViaIdempotencyKey()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"Result":"000","TransToken":"tt-1"}
                """);
        });
        var cache = new InMemoryBhenguDistributedCache();
        var provider = Create(handler, cache: cache);
        var req = new PaymentRequest
        {
            PaymentMethodToken = "ORDER",
            Amount = 1m,
            Currency = "USD",
            Description = "x",
            IdempotencyKey = "idem-1"
        };
        var first = await provider.ProcessPaymentAsync(req);
        var second = await provider.ProcessPaymentAsync(req);
        Assert.Equal(first.GatewayReference, second.GatewayReference);
        Assert.Equal(1, calls);
    }
}
