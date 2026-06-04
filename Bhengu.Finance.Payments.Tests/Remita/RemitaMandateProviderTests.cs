// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Mandate;
using Bhengu.Finance.Payments.Remita.Configuration;
using Bhengu.Finance.Payments.Remita.Internals;
using Bhengu.Finance.Payments.Remita.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Remita;

public class RemitaMandateProviderTests
{
    private static RemitaMandateProvider Create(StubHttpMessageHandler handler)
    {
        var opts = new RemitaOptions
        {
            MerchantId = "MERCH",
            ServiceTypeId = "SVC",
            ApiKey = "API",
            Currency = "NGN"
        };
        var http = new HttpClient(handler);
        var cache = new RemitaIdempotencyCache(new InMemoryBhenguDistributedCache());
        return new RemitaMandateProvider(http, Options.Create(opts), NullLogger<RemitaMandateProvider>.Instance, cache);
    }

    private static MandateRequest SampleRequest(string? idempotencyKey = null) => new()
    {
        CustomerId = "payer@example.com",
        BankAccountToken = "058:0123456789",
        AmountLimit = 5000m,
        Currency = "NGN",
        Description = "Monthly SaaS subscription",
        IdempotencyKey = idempotencyKey
    };

    [Fact]
    public async Task CreateMandateAsync_ReturnsMandate_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("mandate/setup", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"mandateId":"MAN-1","requestId":"REQ-1","status":"pending","statuscode":"025","authorisationUrl":"https://remita/auth/x"}
                """);
        });
        var provider = Create(handler);
        var m = await provider.CreateMandateAsync(SampleRequest());
        Assert.Equal("MAN-1", m.Reference);
        Assert.Equal(MandateStatus.Pending, m.Status);
        Assert.Equal(5000m, m.AmountLimit);
        Assert.Equal("https://remita/auth/x", m.AuthorisationUrl);
    }

    [Fact]
    public async Task CreateMandateAsync_ThrowsBhenguPaymentException_OnBadDestination()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.CreateMandateAsync(SampleRequest() with { BankAccountToken = "no-colon" }));
    }

    [Fact]
    public async Task CreateMandateAsync_Throws429AsProviderRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.CreateMandateAsync(SampleRequest()));
    }

    [Fact]
    public async Task CreateMandateAsync_Throws4xxAsPaymentDeclined()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad bank"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.CreateMandateAsync(SampleRequest()));
    }

    [Fact]
    public async Task CreateMandateAsync_WrapsNetworkFailureAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.CreateMandateAsync(SampleRequest()));
    }

    [Fact]
    public async Task CreateMandateAsync_DedupesOnIdempotencyKey()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"mandateId":"MAN-DUP","status":"pending","statuscode":"025"}
                """);
        });
        var provider = Create(handler);
        var key = $"idemp-{Guid.NewGuid():N}";
        var first = await provider.CreateMandateAsync(SampleRequest(key));
        var second = await provider.CreateMandateAsync(SampleRequest(key));
        Assert.Equal(first.Reference, second.Reference);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ChargeMandateAsync_ReturnsCompleted_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("mandate/debit", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"transactionRef":"DBT-1","status":"success","statuscode":"00"}
                """);
        });
        var provider = Create(handler);
        var resp = await provider.ChargeMandateAsync(new MandateChargeRequest
        {
            MandateReference = "MAN-1",
            Amount = 1000m,
            Currency = "NGN",
            Description = "June bill"
        });
        Assert.Equal("DBT-1", resp.GatewayReference);
        Assert.Equal(Bhengu.Finance.Payments.Core.Models.PaymentStatus.Completed, resp.Status);
    }

    [Fact]
    public async Task CancelMandateAsync_ReturnsCancelled_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("mandate/cancel", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"mandateId":"MAN-1","status":"cancelled","statuscode":"00"}
                """);
        });
        var provider = Create(handler);
        var m = await provider.CancelMandateAsync("MAN-1");
        Assert.Equal(MandateStatus.Cancelled, m.Status);
        Assert.NotNull(m.CancelledAt);
    }
}
