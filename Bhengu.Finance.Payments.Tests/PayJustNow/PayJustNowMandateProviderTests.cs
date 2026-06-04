// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Mandate;
using Bhengu.Finance.Payments.PayJustNow.Configuration;
using Bhengu.Finance.Payments.PayJustNow.Internals;
using Bhengu.Finance.Payments.PayJustNow.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayJustNow;

public class PayJustNowMandateProviderTests
{
    private static PayJustNowMandateProvider Create(StubHttpMessageHandler handler)
    {
        var opts = new PayJustNowOptions { ApiKey = "k", MerchantId = "m", UseSandbox = true };
        var http = new HttpClient(handler);
        return new PayJustNowMandateProvider(http, Options.Create(opts), NullLogger<PayJustNowMandateProvider>.Instance,
            new PayJustNowIdempotencyCache(new InMemoryBhenguDistributedCache()));
    }

    [Fact]
    public async Task CreateMandateAsync_MapsResponse()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"id":"agr_1","shopper_reference":"shop-1","status":"active","amount_limit":150000,"currency":"ZAR","authorised_at":"2026-06-04T00:00:00Z"}"""));
        var provider = Create(handler);
        var m = await provider.CreateMandateAsync(new MandateRequest
        {
            CustomerId = "shop-1",
            BankAccountToken = "tok",
            AmountLimit = 1500m,
            Currency = "ZAR",
            Description = "BNPL agreement"
        });
        Assert.Equal("agr_1", m.Reference);
        Assert.Equal(MandateStatus.Active, m.Status);
        Assert.Equal(1500m, m.AmountLimit);
    }

    [Fact]
    public async Task GetMandateAsync_Returns404AsNull()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "no"));
        var provider = Create(handler);
        Assert.Null(await provider.GetMandateAsync("missing"));
    }

    [Fact]
    public async Task CancelMandateAsync_ReturnsCancelled()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"id":"agr_1","status":"cancelled","cancelled_at":"2026-06-04T00:00:00Z"}"""));
        var provider = Create(handler);
        var m = await provider.CancelMandateAsync("agr_1");
        Assert.Equal(MandateStatus.Cancelled, m.Status);
    }

    [Fact]
    public async Task ChargeMandateAsync_ReturnsCompleted_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"id":"chg_1","status":"succeeded"}"""));
        var provider = Create(handler);
        var resp = await provider.ChargeMandateAsync(new MandateChargeRequest
        {
            MandateReference = "agr_1",
            Amount = 500m,
            Currency = "ZAR",
            Description = "Instalment 1"
        });
        Assert.Equal("chg_1", resp.GatewayReference);
        Assert.Equal(Core.Models.PaymentStatus.Completed, resp.Status);
    }

    [Fact]
    public async Task ChargeMandateAsync_Throws_OnDeclined()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"id":"chg_2","status":"failed","message":"insufficient_funds"}"""));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ChargeMandateAsync(new MandateChargeRequest
        {
            MandateReference = "agr_1",
            Amount = 5000m,
            Currency = "ZAR",
            Description = "x"
        }));
    }

    [Fact]
    public async Task CreateMandateAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.CreateMandateAsync(new MandateRequest
        {
            CustomerId = "s", BankAccountToken = "t", AmountLimit = 1m, Currency = "ZAR", Description = "x"
        }));
    }
}
