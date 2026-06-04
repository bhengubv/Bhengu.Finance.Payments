// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Mandate;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Stripe;

[Collection(StripeConfigurationCollection.Name)]
public class StripeMandateProviderTests
{
    private static StripeMandateProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new StripeOptions { SecretKey = "sk_test_fake", WebhookSecret = "whsec_test_fake" }),
            NullLogger<StripeMandateProvider>.Instance);

    [Fact]
    public async Task CreateMandateAsync_HostedFlow_ReturnsAuthorisationUrl()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("setup_intents", req.RequestUri!.PathAndQuery, StringComparison.Ordinal);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"seti_1","object":"setup_intent","customer":"cus_1","status":"requires_action","next_action":{"type":"redirect_to_url","redirect_to_url":{"url":"https://hooks.stripe.com/setup_intents/seti_1/redirect","return_url":"https://example.com"}}}
                """);
        });
        var provider = Create(handler);
        var mandate = await provider.CreateMandateAsync(new MandateRequest
        {
            CustomerId = "cus_1",
            BankAccountToken = "",
            AmountLimit = 100m,
            Currency = "EUR",
            Description = "Monthly SEPA debit"
        });

        Assert.Equal("seti_1", mandate.Reference);
        Assert.Equal(MandateStatus.Pending, mandate.Status);
        Assert.NotNull(mandate.AuthorisationUrl);
    }

    [Fact]
    public async Task CreateMandateAsync_WithBankToken_ConfirmsAndReturnsMandate()
    {
        var fetchedMandate = false;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("/mandates/", StringComparison.Ordinal))
            {
                fetchedMandate = true;
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"id":"mandate_1","object":"mandate","status":"active","payment_method":"pm_1","customer_acceptance":{"type":"online","accepted_at":1700000000,"online":{"ip_address":"1.2.3.4","user_agent":"x"}}}
                    """);
            }
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"seti_2","object":"setup_intent","customer":"cus_2","status":"succeeded","mandate":"mandate_1","payment_method":"pm_1"}
                """);
        });
        var provider = Create(handler);
        var mandate = await provider.CreateMandateAsync(new MandateRequest
        {
            CustomerId = "cus_2",
            BankAccountToken = "pm_1",
            AmountLimit = 250m,
            Currency = "GBP",
            Description = "Subscription"
        });

        Assert.True(fetchedMandate);
        Assert.Equal("mandate_1", mandate.Reference);
        Assert.Equal(MandateStatus.Active, mandate.Status);
    }

    [Fact]
    public async Task GetMandateAsync_ReturnsNull_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.NotFound, """
            {"error":{"type":"invalid_request_error","message":"No such mandate"}}
            """));
        var provider = Create(handler);
        Assert.Null(await provider.GetMandateAsync("mandate_missing"));
    }

    [Fact]
    public async Task CancelMandateAsync_OnSetupIntent_CallsCancelEndpoint()
    {
        var sawCancel = false;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("/cancel", StringComparison.Ordinal)) sawCancel = true;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"seti_3","object":"setup_intent","customer":"cus_3","status":"canceled"}
                """);
        });
        var provider = Create(handler);
        var mandate = await provider.CancelMandateAsync("seti_3");
        Assert.True(sawCancel);
        Assert.Equal(MandateStatus.Cancelled, mandate.Status);
    }

    [Fact]
    public async Task CancelMandateAsync_NotFound_ReturnsCancelledAsIdempotent()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.NotFound, """
            {"error":{"type":"invalid_request_error","message":"No such setup intent"}}
            """));
        var provider = Create(handler);
        var mandate = await provider.CancelMandateAsync("seti_gone");
        Assert.Equal(MandateStatus.Cancelled, mandate.Status);
    }

    [Fact]
    public async Task ChargeMandateAsync_CreatesOffSessionPaymentIntent()
    {
        var sawOffSession = false;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("/setup_intents/", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"id":"seti_4","object":"setup_intent","customer":"cus_4","status":"succeeded","mandate":"mandate_4","payment_method":"pm_4"}
                    """);
            }
            if (req.RequestUri!.PathAndQuery.Contains("payment_intents", StringComparison.Ordinal))
            {
                var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                if (body.Contains("off_session", StringComparison.Ordinal)) sawOffSession = true;
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"id":"pi_mandate_1","object":"payment_intent","amount":7500,"currency":"eur","status":"succeeded"}
                    """);
            }
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        });
        var provider = Create(handler);
        var response = await provider.ChargeMandateAsync(new MandateChargeRequest
        {
            MandateReference = "seti_4",
            Amount = 75m,
            Currency = "EUR",
            Description = "Monthly debit"
        });

        Assert.True(sawOffSession);
        Assert.Equal("pi_mandate_1", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Equal(75m, response.Amount);
    }

    [Fact]
    public async Task ChargeMandateAsync_BankDeclined_Throws4xx()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("/setup_intents/", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"id":"seti_5","object":"setup_intent","customer":"cus_5","status":"succeeded","mandate":"mandate_5","payment_method":"pm_5"}
                    """);
            }
            return StubHttpMessageHandler.Json(HttpStatusCode.PaymentRequired, """
                {"error":{"type":"card_error","code":"insufficient_funds","message":"Bank declined"}}
                """);
        });
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ChargeMandateAsync(new MandateChargeRequest
        {
            MandateReference = "seti_5",
            Amount = 100m,
            Currency = "EUR",
            Description = "x"
        }));
    }

    [Fact]
    public async Task CreateMandateAsync_Throws5xxAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.BadGateway, """
            {"error":{"type":"api_error","message":"Stripe is down"}}
            """));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.CreateMandateAsync(new MandateRequest
        {
            CustomerId = "cus_x",
            BankAccountToken = "",
            AmountLimit = 50m,
            Currency = "EUR",
            Description = "x"
        }));
    }
}
