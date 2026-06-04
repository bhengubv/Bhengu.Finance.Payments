// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Mandate;
using Bhengu.Finance.Payments.Mukuru.Configuration;
using Bhengu.Finance.Payments.Mukuru.Internals;
using Bhengu.Finance.Payments.Mukuru.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Mukuru;

public class MukuruMandateProviderTests
{
    private static MukuruMandateProvider Create(StubHttpMessageHandler handler)
    {
        var opts = new MukuruOptions
        {
            ClientId = "c",
            ClientSecret = "s",
            SenderCountry = "ZA",
            DefaultCurrency = "ZAR"
        };
        var http = new HttpClient(handler);
        var payment = new MukuruPaymentProvider(http, Options.Create(opts), NullLogger<MukuruPaymentProvider>.Instance,
            new MukuruIdempotencyCache(new InMemoryBhenguDistributedCache()));
        return new MukuruMandateProvider(payment, Options.Create(opts), NullLogger<MukuruMandateProvider>.Instance,
            new MukuruIdempotencyCache(new InMemoryBhenguDistributedCache()));
    }

    private static StubHttpMessageHandler WithToken(Func<HttpRequestMessage, HttpResponseMessage> apiHandler) =>
        new((req, _) => req.RequestUri!.PathAndQuery.Contains("auth/token", StringComparison.OrdinalIgnoreCase)
            ? StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"access_token":"tok","token_type":"Bearer","expires_in":3600}""")
            : apiHandler(req));

    [Fact]
    public async Task CreateMandateAsync_MapsResponse()
    {
        var handler = WithToken(req =>
        {
            Assert.Contains("send/recurring", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"man_1","shopper_reference":"shop-1","status":"active","amount_limit":"500.00","currency":"ZAR","authorised_at":"2026-06-04T00:00:00Z"}""");
        });
        var provider = Create(handler);
        var m = await provider.CreateMandateAsync(new MandateRequest
        {
            CustomerId = "shop-1",
            BankAccountToken = "bank-tok",
            AmountLimit = 500m,
            Currency = "ZAR",
            Description = "Mukuru Send recurring"
        });
        Assert.Equal("man_1", m.Reference);
        Assert.Equal(MandateStatus.Active, m.Status);
        Assert.Equal(500m, m.AmountLimit);
    }

    [Fact]
    public async Task GetMandateAsync_Returns404AsNull()
    {
        var handler = WithToken(_ => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "no"));
        var provider = Create(handler);
        Assert.Null(await provider.GetMandateAsync("missing"));
    }

    [Fact]
    public async Task CancelMandateAsync_ReturnsCancelled()
    {
        var handler = WithToken(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"id":"man_1","status":"cancelled","cancelled_at":"2026-06-04T00:00:00Z"}"""));
        var provider = Create(handler);
        var m = await provider.CancelMandateAsync("man_1");
        Assert.Equal(MandateStatus.Cancelled, m.Status);
    }

    [Fact]
    public async Task ChargeMandateAsync_ReturnsCompleted_OnSuccess()
    {
        var handler = WithToken(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"transaction_id":"chg_1","status":"completed"}"""));
        var provider = Create(handler);
        var resp = await provider.ChargeMandateAsync(new MandateChargeRequest
        {
            MandateReference = "man_1",
            Amount = 250m,
            Currency = "ZAR",
            Description = "Recurring debit"
        });
        Assert.Equal("chg_1", resp.GatewayReference);
        Assert.Equal(Core.Models.PaymentStatus.Completed, resp.Status);
    }

    [Fact]
    public async Task ChargeMandateAsync_Throws_OnDeclined()
    {
        var handler = WithToken(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"transaction_id":"chg_2","status":"failed","message":"insufficient_funds"}"""));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ChargeMandateAsync(new MandateChargeRequest
        {
            MandateReference = "man_1",
            Amount = 5000m,
            Currency = "ZAR",
            Description = "x"
        }));
    }

    [Fact]
    public async Task CreateMandateAsync_Throws429AsRateLimit()
    {
        var handler = WithToken(_ => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.CreateMandateAsync(new MandateRequest
        {
            CustomerId = "s", BankAccountToken = "t", AmountLimit = 1m, Currency = "ZAR", Description = "x"
        }));
    }
}
