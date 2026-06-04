// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.OPay.Configuration;
using Bhengu.Finance.Payments.OPay.Internals;
using Bhengu.Finance.Payments.OPay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.OPay;

public class OPayTokenisationProviderTests
{
    private static OPayTokenisationProvider Create(StubHttpMessageHandler handler, OPayOptions? opts = null)
    {
        opts ??= new OPayOptions
        {
            PublicKey = "pub",
            SecretKey = "sec",
            MerchantId = "MERCH",
            Country = "NG",
            CallbackUrl = "https://example.com/cb",
            ReturnUrl = "https://example.com/ret"
        };
        var http = new HttpClient(handler);
        var cache = new OPayIdempotencyCache(new InMemoryBhenguDistributedCache());
        return new OPayTokenisationProvider(http, Options.Create(opts),
            NullLogger<OPayTokenisationProvider>.Instance, cache);
    }

    private static TokeniseRequest SampleRequest(string? idempotencyKey = null) => new()
    {
        Card = new CardDetails
        {
            CardholderName = "Test User",
            CardNumber = "0123456789",       // bank account number
            BillingAddressLine1 = "058",     // bank code (CBN)
            ExpiryMonth = 1,
            ExpiryYear = 2099
        },
        CustomerId = "USR-001",
        DisplayName = "Default Bank",
        SetAsDefault = true,
        IdempotencyKey = idempotencyKey
    };

    [Fact]
    public async Task TokeniseAsync_ReturnsPaymentMethod_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("savedBankAccount/register", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code":"00000","message":"OK","data":{"token":"opay-bank-tok","userId":"USR-001","bankAccountNumber":"0123456789","bankCode":"058","bankName":"GTBank","alias":"Default Bank","isDefault":true}}
                """);
        });
        var provider = Create(handler);
        var pm = await provider.TokeniseAsync(SampleRequest());

        Assert.Equal("opay-bank-tok", pm.Token);
        Assert.Equal("USR-001", pm.CustomerId);
        Assert.Equal(PaymentMethodKind.BankAccount, pm.Kind);
        Assert.Equal("GTBank", pm.Brand);
        Assert.Equal("6789", pm.Last4);
        Assert.True(pm.IsDefault);
    }

    [Fact]
    public async Task TokeniseAsync_ThrowsPaymentDeclined_WhenEnvelopeNotSuccess()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"code":"40001","message":"bank not supported"}"""));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_Throws429AsProviderRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_WrapsNetworkFailureAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.TokeniseAsync(SampleRequest()));
    }

    [Fact]
    public async Task TokeniseAsync_DedupesOnIdempotencyKey()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code":"00000","message":"OK","data":{"token":"opay-bank-dup","userId":"USR-001","bankAccountNumber":"0123456789","bankCode":"058","bankName":"GT","isDefault":false}}
                """);
        });
        var provider = Create(handler);
        var key = $"idemp-{Guid.NewGuid():N}";
        var first = await provider.TokeniseAsync(SampleRequest(key));
        var second = await provider.TokeniseAsync(SampleRequest(key));
        Assert.Equal(first.Token, second.Token);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ListPaymentMethodsAsync_ReturnsMappedAccounts()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"code":"00000","data":{"accounts":[
                    {"token":"t1","bankAccountNumber":"0001111111","bankCode":"058","bankName":"GT","isDefault":true},
                    {"token":"t2","bankAccountNumber":"0002222222","bankCode":"044","bankName":"Access"}
                ]}}
                """));
        var provider = Create(handler);
        var list = await provider.ListPaymentMethodsAsync("USR-001");
        Assert.Equal(2, list.Count);
        Assert.Equal("1111", list[0].Last4);
        Assert.True(list[0].IsDefault);
        Assert.Equal("Access", list[1].Brand);
    }

    [Fact]
    public async Task DeletePaymentMethodAsync_ReturnsTrue_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"code":"00000","message":"OK"}"""));
        var provider = Create(handler);
        Assert.True(await provider.DeletePaymentMethodAsync("t1"));
    }
}
