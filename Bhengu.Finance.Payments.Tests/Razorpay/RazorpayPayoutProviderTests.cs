// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Bhengu.Finance.Payments.Razorpay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Razorpay;

public class RazorpayPayoutProviderTests
{
    private static RazorpayPayoutProvider Create(StubHttpMessageHandler handler, RazorpayOptions? opts = null) =>
        new(new HttpClient(handler),
            Options.Create(opts ?? new RazorpayOptions { KeyId = "rzp_test_x", KeySecret = "secret_x", RazorpayXAccountNumber = "2323230099089860" }),
            NullLogger<RazorpayPayoutProvider>.Instance);

    private static PayoutRequest SampleRequest() => new()
    {
        DestinationToken = "fa_test123",
        Amount = 500m,
        Currency = "INR",
        Description = "Vendor payout"
    };

    [Fact]
    public async Task ProcessPayoutAsync_PostsAndMapsResponse()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("v1/payouts", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"pout_1","entity":"payout","amount":50000,"currency":"INR","status":"processed","mode":"IMPS"}""");
        });
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(SampleRequest());

        Assert.Equal("pout_1", payout.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payout.Status);
        Assert.Equal(500m, payout.Amount);
        Assert.Equal("INR", payout.Currency);
    }

    [Fact]
    public async Task ProcessPayoutAsync_PassesIdempotencyKey()
    {
        string? header = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            header = req.Headers.TryGetValues("X-Razorpay-IdempotencyKey", out var v) ? string.Join(",", v) : null;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"pout_2","entity":"payout","amount":50000,"currency":"INR","status":"processed"}""");
        });
        var provider = Create(handler);
        var req = SampleRequest() with { IdempotencyKey = "idem-payout" };
        await provider.ProcessPayoutAsync(req);
        Assert.Equal("idem-payout", header);
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws_WhenAccountNumberMissing()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")),
            new RazorpayOptions { KeyId = "x", KeySecret = "y", RazorpayXAccountNumber = "" });
        await Assert.ThrowsAsync<ProviderConfigurationException>(() => provider.ProcessPayoutAsync(SampleRequest()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws_OnRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "throttled"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ProcessPayoutAsync(SampleRequest()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws_On4xx()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad fund_account_id"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPayoutAsync(SampleRequest()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_Throws_On5xx()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.InternalServerError, "boom"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPayoutAsync(SampleRequest()));
    }

    [Fact]
    public async Task ProcessPayoutAsync_MapsQueuedToPending()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"pout_3","entity":"payout","status":"queued","amount":50000,"currency":"INR"}"""));
        var provider = Create(handler);
        var payout = await provider.ProcessPayoutAsync(SampleRequest());
        Assert.Equal(PaymentStatus.Pending, payout.Status);
    }

    [Fact]
    public async Task ProcessPayoutAsync_OnNetworkFailure_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPayoutAsync(SampleRequest()));
    }
}
