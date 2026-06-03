// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Mandate;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.TymeBank.Configuration;
using Bhengu.Finance.Payments.TymeBank.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.TymeBank;

public class TymeBankMandateProviderTests
{
    private static TymeBankMandateProvider Create(StubHttpMessageHandler handler, TymeBankOptions? opts = null)
    {
        opts ??= new TymeBankOptions
        {
            ClientId = "tyme-client",
            ClientSecret = "tyme-secret",
            MerchantId = "MERCH-001",
            WebhookSecret = "webhook-tyme-secret",
            Currency = "ZAR",
            CallbackUrl = "https://example.com/tyme-callback"
        };
        var http = new HttpClient(handler);
        return new TymeBankMandateProvider(http, Options.Create(opts), NullLogger<TymeBankMandateProvider>.Instance);
    }

    private static StubHttpMessageHandler HandlerWithTokenAnd(
        Func<HttpRequestMessage, HttpResponseMessage> apiHandler) =>
        new((req, _) => req.RequestUri!.PathAndQuery.Contains("oauth2/token", StringComparison.OrdinalIgnoreCase)
            ? StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"access_token":"tyme-tok-123","token_type":"Bearer","expires_in":3600}
                """)
            : apiHandler(req));

    private static MandateRequest SampleMandate() => new()
    {
        CustomerId = "cust_001",
        // TymeBank-style "accountNumber:branchCode:accountHolder" token
        BankAccountToken = "1234567890:678910:Jane Customer",
        AmountLimit = 2500m,
        Currency = "ZAR",
        Description = "Insurance premium"
    };

    [Fact]
    public void Constructor_Throws_WhenClientIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new TymeBankMandateProvider(http, Options.Create(new TymeBankOptions { ClientSecret = "s" }),
                NullLogger<TymeBankMandateProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenClientSecretMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new TymeBankMandateProvider(http, Options.Create(new TymeBankOptions { ClientId = "c" }),
                NullLogger<TymeBankMandateProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsTymeBank()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("tymebank", provider.ProviderName);
    }

    [Fact]
    public async Task CreateMandateAsync_ReturnsPendingMandate_OnSuccess()
    {
        string? requestBody = null;
        var handler = HandlerWithTokenAnd(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("v1/mandates", req.RequestUri!.PathAndQuery);
            requestBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"mandate_id":"mand_tyme_1","customer_reference":"cust_001","status":"pending","amount_limit":"2500.00","currency":"ZAR","authorisation_url":"https://app.tymebank.co.za/authorise/mand_tyme_1"}
                """);
        });
        var provider = Create(handler);
        var mandate = await provider.CreateMandateAsync(SampleMandate());

        Assert.Equal("mand_tyme_1", mandate.Reference);
        Assert.Equal(MandateStatus.Pending, mandate.Status);
        Assert.Equal("https://app.tymebank.co.za/authorise/mand_tyme_1", mandate.AuthorisationUrl);
        Assert.Equal(2500m, mandate.AmountLimit);
        Assert.Equal("ZAR", mandate.Currency);
        Assert.NotNull(requestBody);
        Assert.Contains("\"account_number\":\"1234567890\"", requestBody!);
        Assert.Contains("\"branch_code\":\"678910\"", requestBody);
        Assert.Contains("\"account_holder\":\"Jane Customer\"", requestBody);
    }

    [Fact]
    public async Task CreateMandateAsync_AcceptsJsonBankAccountToken()
    {
        string? requestBody = null;
        var handler = HandlerWithTokenAnd(req =>
        {
            requestBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"mandate_id":"mand_2","customer_reference":"cust_001","status":"pending","amount_limit":"500.00","currency":"ZAR"}
                """);
        });
        var provider = Create(handler);
        await provider.CreateMandateAsync(new MandateRequest
        {
            CustomerId = "cust_001",
            BankAccountToken = """{"accountNumber":"9999999999","branchCode":"123456","accountHolder":"Alex"}""",
            AmountLimit = 500m,
            Currency = "ZAR",
            Description = "test"
        });

        Assert.NotNull(requestBody);
        Assert.Contains("\"account_number\":\"9999999999\"", requestBody!);
        Assert.Contains("\"branch_code\":\"123456\"", requestBody);
        Assert.Contains("\"account_holder\":\"Alex\"", requestBody);
    }

    [Fact]
    public async Task CreateMandateAsync_PassesIdempotencyKey_AsHeader()
    {
        string? header = null;
        var handler = HandlerWithTokenAnd(req =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("v1/mandates", StringComparison.Ordinal))
                header = req.Headers.TryGetValues("Idempotency-Key", out var v) ? string.Join(",", v) : null;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"mandate_id":"mand_3","customer_reference":"cust_001","status":"pending","amount_limit":"100.00","currency":"ZAR"}
                """);
        });
        var provider = Create(handler);
        await provider.CreateMandateAsync(new MandateRequest
        {
            CustomerId = "cust_001",
            BankAccountToken = "1:2:3",
            AmountLimit = 100m,
            Currency = "ZAR",
            Description = "x",
            IdempotencyKey = "idem-mandate-1"
        });
        Assert.Equal("idem-mandate-1", header);
    }

    [Fact]
    public async Task GetMandateAsync_ReturnsActive_WhenStatusIsActive()
    {
        var handler = HandlerWithTokenAnd(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains("v1/mandates/mand_tyme_1", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"mandate_id":"mand_tyme_1","customer_reference":"cust_001","status":"active","amount_limit":"2500.00","currency":"ZAR","authorised_at":"2026-04-01T10:00:00Z"}
                """);
        });
        var provider = Create(handler);
        var mandate = await provider.GetMandateAsync("mand_tyme_1");

        Assert.NotNull(mandate);
        Assert.Equal(MandateStatus.Active, mandate!.Status);
        Assert.Equal(2500m, mandate.AmountLimit);
        Assert.NotNull(mandate.AuthorisedAt);
    }

    [Fact]
    public async Task GetMandateAsync_ReturnsNull_OnNotFound()
    {
        var handler = HandlerWithTokenAnd(_ =>
            StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.Null(await provider.GetMandateAsync("nope"));
    }

    [Fact]
    public async Task CancelMandateAsync_IssuesDelete_AndReturnsCancelled()
    {
        var handler = HandlerWithTokenAnd(req =>
        {
            Assert.Equal(HttpMethod.Delete, req.Method);
            Assert.Contains("v1/mandates/mand_tyme_1", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"mandate_id":"mand_tyme_1","customer_reference":"cust_001","status":"cancelled","amount_limit":"2500.00","currency":"ZAR","cancelled_at":"2026-04-15T12:00:00Z"}
                """);
        });
        var provider = Create(handler);
        var mandate = await provider.CancelMandateAsync("mand_tyme_1");

        Assert.Equal("mand_tyme_1", mandate.Reference);
        Assert.Equal(MandateStatus.Cancelled, mandate.Status);
        Assert.NotNull(mandate.CancelledAt);
    }

    [Fact]
    public async Task CancelMandateAsync_IsIdempotent_WhenMandateMissing()
    {
        var handler = HandlerWithTokenAnd(_ =>
            StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "mandate not found"));
        var provider = Create(handler);
        var mandate = await provider.CancelMandateAsync("gone");
        Assert.Equal(MandateStatus.Cancelled, mandate.Status);
    }

    [Fact]
    public async Task ChargeMandateAsync_PostsDebit_AndReturnsCompleted()
    {
        var handler = HandlerWithTokenAnd(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("v1/mandates/mand_tyme_1/debit", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"debit_id":"deb_t1","status":"completed"}
                """);
        });
        var provider = Create(handler);
        var payment = await provider.ChargeMandateAsync(new MandateChargeRequest
        {
            MandateReference = "mand_tyme_1",
            Amount = 199m,
            Currency = "ZAR",
            Description = "March premium"
        });

        Assert.Equal("deb_t1", payment.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(199m, payment.Amount);
        Assert.Equal("ZAR", payment.Currency);
    }

    [Fact]
    public async Task ChargeMandateAsync_OnDecline_ThrowsPaymentDeclinedException()
    {
        // TymeBank returns 422 when mandate inactive or amount-over-limit.
        var handler = HandlerWithTokenAnd(_ =>
            StubHttpMessageHandler.Text(HttpStatusCode.UnprocessableEntity,
                """{"error":"amount exceeds mandate limit"}"""));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            provider.ChargeMandateAsync(new MandateChargeRequest
            {
                MandateReference = "mand_tyme_1",
                Amount = 1_000_000m,
                Currency = "ZAR",
                Description = "over"
            }));
    }

    [Fact]
    public async Task ChargeMandateAsync_OnNotFound_ThrowsPaymentDeclined()
    {
        // 404 on a non-existent mandate translates to PaymentDeclined (4xx family).
        var handler = HandlerWithTokenAnd(_ =>
            StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "mandate not active"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            provider.ChargeMandateAsync(new MandateChargeRequest
            {
                MandateReference = "gone",
                Amount = 100m,
                Currency = "ZAR",
                Description = "x"
            }));
    }

    [Fact]
    public async Task ChargeMandateAsync_On5xx_ThrowsProviderUnavailable()
    {
        var handler = HandlerWithTokenAnd(_ =>
            StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            provider.ChargeMandateAsync(new MandateChargeRequest
            {
                MandateReference = "mand_tyme_1",
                Amount = 100m,
                Currency = "ZAR",
                Description = "x"
            }));
    }

    [Fact]
    public async Task ChargeMandateAsync_OnNetworkFailure_ThrowsProviderUnavailable()
    {
        // First call (token) succeeds; subsequent calls fail.
        var tokenSeen = false;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (!tokenSeen && req.RequestUri!.PathAndQuery.Contains("oauth2/token", StringComparison.OrdinalIgnoreCase))
            {
                tokenSeen = true;
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"access_token":"tyme-tok-123","token_type":"Bearer","expires_in":3600}
                    """);
            }
            throw new HttpRequestException("net");
        });
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            provider.ChargeMandateAsync(new MandateChargeRequest
            {
                MandateReference = "mand_tyme_1",
                Amount = 100m,
                Currency = "ZAR",
                Description = "x"
            }));
    }
}
