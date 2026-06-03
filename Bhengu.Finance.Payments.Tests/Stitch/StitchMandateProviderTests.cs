// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Mandate;
using Bhengu.Finance.Payments.Stitch.Configuration;
using Bhengu.Finance.Payments.Stitch.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Stitch;

public class StitchMandateProviderTests
{
    private static StitchMandateProvider Create(StubHttpMessageHandler handler, StitchOptions? opts = null)
    {
        opts ??= new StitchOptions
        {
            ClientId = "stitch-client",
            ClientSecret = "stitch-secret",
            ApiKey = "stitch-key",
            WebhookSecret = "webhook-stitch-secret",
            Currency = "ZAR"
        };
        var http = new HttpClient(handler);
        return new StitchMandateProvider(http, Options.Create(opts), NullLogger<StitchMandateProvider>.Instance);
    }

    /// <summary>
    /// Build a handler that answers Stitch's OAuth2 token endpoint with a fixed access_token
    /// and dispatches GraphQL requests to the supplied API handler. This is how all mandate
    /// operations get their bearer token — the provider transparently re-uses the cached
    /// access_token until 60 seconds before expiry.
    /// </summary>
    private static StubHttpMessageHandler HandlerWithTokenAnd(
        Func<HttpRequestMessage, HttpResponseMessage> apiHandler) =>
        new((req, _) => req.RequestUri!.PathAndQuery.Contains("connect/token", StringComparison.OrdinalIgnoreCase) ||
                        req.RequestUri.Host.Contains("secure.stitch.money", StringComparison.OrdinalIgnoreCase)
            ? StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"access_token":"stitch-tok-123","token_type":"Bearer","expires_in":3600}
                """)
            : apiHandler(req));

    private static MandateRequest SampleMandate() => new()
    {
        CustomerId = "cust_001",
        BankAccountToken = string.Empty,
        AmountLimit = 1500m,
        Currency = "ZAR",
        Description = "Monthly subscription"
    };

    [Fact]
    public void Constructor_Throws_WhenClientIdMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new StitchMandateProvider(http, Options.Create(new StitchOptions { ClientSecret = "s" }),
                NullLogger<StitchMandateProvider>.Instance));
    }

    [Fact]
    public void Constructor_Throws_WhenClientSecretMissing()
    {
        var http = new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Throws<ProviderConfigurationException>(() =>
            new StitchMandateProvider(http, Options.Create(new StitchOptions { ClientId = "c" }),
                NullLogger<StitchMandateProvider>.Instance));
    }

    [Fact]
    public void ProviderName_IsStitch()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        Assert.Equal("stitch", provider.ProviderName);
    }

    [Fact]
    public async Task CreateMandateAsync_ReturnsPendingWithAuthorisationUrl_OnSuccess()
    {
        var handler = HandlerWithTokenAnd(req =>
        {
            Assert.Contains("graphql", req.RequestUri!.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":{"createPaymentInitiation":{"paymentInitiation":{"id":"pi_debicheck_1","authorizationUrl":"https://secure.stitch.money/auth/pi_debicheck_1","status":"pending"}}}}
                """);
        });
        var provider = Create(handler);
        var mandate = await provider.CreateMandateAsync(SampleMandate());

        Assert.Equal("pi_debicheck_1", mandate.Reference);
        Assert.Equal(MandateStatus.Pending, mandate.Status);
        Assert.Equal("https://secure.stitch.money/auth/pi_debicheck_1", mandate.AuthorisationUrl);
        Assert.Equal(1500m, mandate.AmountLimit);
        Assert.Equal("ZAR", mandate.Currency);
        Assert.Equal("cust_001", mandate.CustomerId);
    }

    [Fact]
    public async Task CreateMandateAsync_SendsBearerToken_FromOAuthExchange()
    {
        string? auth = null;
        var handler = HandlerWithTokenAnd(req =>
        {
            auth = req.Headers.Authorization?.Parameter;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":{"createPaymentInitiation":{"paymentInitiation":{"id":"pi_1","authorizationUrl":"https://x","status":"pending"}}}}
                """);
        });
        var provider = Create(handler);
        await provider.CreateMandateAsync(SampleMandate());
        Assert.Equal("stitch-tok-123", auth);
    }

    [Fact]
    public async Task GetMandateAsync_ReturnsActive_WhenStatusIsActive()
    {
        var handler = HandlerWithTokenAnd(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":{"node":{"id":"pi_42","status":"active","authorizationUrl":null,"amount":{"quantity":"1500.00","currency":"ZAR"},"payer":{"reference":"cust_001"}}}}
                """));
        var provider = Create(handler);
        var mandate = await provider.GetMandateAsync("pi_42");

        Assert.NotNull(mandate);
        Assert.Equal("pi_42", mandate!.Reference);
        Assert.Equal(MandateStatus.Active, mandate.Status);
        Assert.Equal(1500m, mandate.AmountLimit);
        Assert.NotNull(mandate.AuthorisedAt);
    }

    [Fact]
    public async Task GetMandateAsync_ReturnsNull_OnNotFound()
    {
        var handler = HandlerWithTokenAnd(_ =>
            StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.Null(await provider.GetMandateAsync("nonexistent"));
    }

    [Fact]
    public async Task CancelMandateAsync_ReturnsCancelled_OnSuccess()
    {
        var handler = HandlerWithTokenAnd(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":{"cancelPaymentInitiation":{"paymentInitiation":{"id":"pi_99","status":"cancelled"}}}}
                """));
        var provider = Create(handler);
        var mandate = await provider.CancelMandateAsync("pi_99");

        Assert.Equal("pi_99", mandate.Reference);
        Assert.Equal(MandateStatus.Cancelled, mandate.Status);
        Assert.NotNull(mandate.CancelledAt);
    }

    [Fact]
    public async Task CancelMandateAsync_IsIdempotent_WhenAlreadyCancelled()
    {
        // Stitch returns 400 with "already cancelled" message — the provider must succeed silently.
        var handler = HandlerWithTokenAnd(_ =>
            StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "mandate already cancelled"));
        var provider = Create(handler);
        var mandate = await provider.CancelMandateAsync("pi_already");

        Assert.Equal("pi_already", mandate.Reference);
        Assert.Equal(MandateStatus.Cancelled, mandate.Status);
    }

    [Fact]
    public async Task ChargeMandateAsync_ReturnsCompleted_OnDebitSuccess()
    {
        var handler = HandlerWithTokenAnd(req =>
        {
            Assert.Contains("graphql", req.RequestUri!.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":{"paymentInitiationDebit":{"debit":{"id":"debit_777","status":"completed"}}}}
                """);
        });
        var provider = Create(handler);
        var payment = await provider.ChargeMandateAsync(new MandateChargeRequest
        {
            MandateReference = "pi_42",
            Amount = 250m,
            Currency = "ZAR",
            Description = "March premium"
        });

        Assert.Equal("debit_777", payment.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(250m, payment.Amount);
        Assert.Equal("ZAR", payment.Currency);
    }

    [Fact]
    public async Task ChargeMandateAsync_OnDecline_ThrowsPaymentDeclinedException()
    {
        var handler = HandlerWithTokenAnd(_ =>
            StubHttpMessageHandler.Text(HttpStatusCode.UnprocessableEntity,
                """{"errors":[{"message":"Insufficient funds"}]}"""));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            provider.ChargeMandateAsync(new MandateChargeRequest
            {
                MandateReference = "pi_42",
                Amount = 1_000_000m,
                Currency = "ZAR",
                Description = "Over limit"
            }));
    }

    [Fact]
    public async Task ChargeMandateAsync_On5xx_ThrowsProviderUnavailableException()
    {
        var handler = HandlerWithTokenAnd(_ =>
            StubHttpMessageHandler.Text(HttpStatusCode.BadGateway, "upstream"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            provider.ChargeMandateAsync(new MandateChargeRequest
            {
                MandateReference = "pi_42",
                Amount = 100m,
                Currency = "ZAR",
                Description = "x"
            }));
    }

    [Fact]
    public async Task ChargeMandateAsync_OnNetworkFailure_ThrowsProviderUnavailable()
    {
        // The token endpoint succeeds the first time so the provider has a cached token, then the
        // GraphQL call dies with a network error.
        var graphqlSeen = false;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("connect/token", StringComparison.OrdinalIgnoreCase) ||
                req.RequestUri.Host.Contains("secure.stitch.money", StringComparison.OrdinalIgnoreCase))
            {
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"access_token":"stitch-tok-123","token_type":"Bearer","expires_in":3600}
                    """);
            }
            graphqlSeen = true;
            throw new HttpRequestException("dns failed");
        });
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            provider.ChargeMandateAsync(new MandateChargeRequest
            {
                MandateReference = "pi_42",
                Amount = 100m,
                Currency = "ZAR",
                Description = "x"
            }));
        Assert.True(graphqlSeen);
    }

    [Fact]
    public async Task TokenIsCached_BetweenCalls()
    {
        var tokenCalls = 0;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("connect/token", StringComparison.OrdinalIgnoreCase) ||
                req.RequestUri.Host.Contains("secure.stitch.money", StringComparison.OrdinalIgnoreCase))
            {
                tokenCalls++;
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"access_token":"stitch-tok-123","token_type":"Bearer","expires_in":3600}
                    """);
            }
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":{"createPaymentInitiation":{"paymentInitiation":{"id":"pi_n","authorizationUrl":"u","status":"pending"}}}}
                """);
        });
        var provider = Create(handler);
        await provider.CreateMandateAsync(SampleMandate());
        await provider.CreateMandateAsync(SampleMandate());
        await provider.CreateMandateAsync(SampleMandate());
        Assert.Equal(1, tokenCalls);
    }
}
