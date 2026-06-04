// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Marketplace;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Stripe;

[Collection(StripeConfigurationCollection.Name)]
public class StripeMarketplaceProviderTests
{
    private static StripeMarketplaceProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new StripeOptions { SecretKey = "sk_test_fake", WebhookSecret = "whsec_test_fake" }),
            NullLogger<StripeMarketplaceProvider>.Instance);

    [Fact]
    public async Task CreateSubAccountAsync_ReturnsAccountAndOnboardingUrl()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("/account_links", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"object":"account_link","created":1700000000,"expires_at":1700003600,"url":"https://connect.stripe.com/setup/c/acct_1"}
                    """);
            }
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"acct_1","object":"account","type":"express","country":"US","email":"shop@example.com","business_profile":{"name":"Shop A"},"charges_enabled":false,"payouts_enabled":false}
                """);
        });
        var provider = Create(handler);
        var account = await provider.CreateSubAccountAsync(new SubAccountRequest
        {
            BusinessName = "Shop A",
            ContactEmail = "shop@example.com",
            Country = "US",
            ReturnUrl = "https://example.com/return"
        });

        Assert.Equal("acct_1", account.Reference);
        Assert.Equal("Shop A", account.BusinessName);
        Assert.Equal("https://connect.stripe.com/setup/c/acct_1", account.OnboardingUrl);
    }

    [Fact]
    public async Task GetSubAccountAsync_ReturnsNull_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.NotFound, """
            {"error":{"type":"invalid_request_error","message":"No such account"}}
            """));
        var provider = Create(handler);
        Assert.Null(await provider.GetSubAccountAsync("acct_missing"));
    }

    [Fact]
    public async Task ListSubAccountsAsync_ReturnsCollection()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"object":"list","data":[
                {"id":"acct_a","object":"account","type":"express","country":"US","email":"a@x.com","business_profile":{"name":"A"},"charges_enabled":true,"payouts_enabled":true},
                {"id":"acct_b","object":"account","type":"express","country":"GB","email":"b@x.com","business_profile":{"name":"B"},"charges_enabled":false,"payouts_enabled":false}
            ],"has_more":false}
            """));
        var provider = Create(handler);
        var accounts = await provider.ListSubAccountsAsync().ToListAsync();
        Assert.Equal(2, accounts.Count);
        Assert.True(accounts[0].IsActive);
        Assert.False(accounts[1].IsActive);
    }

    [Fact]
    public async Task CreateSplitAsync_PersistsLocallyAndGetReturnsIt()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var provider = Create(handler);
        var split = await provider.CreateSplitAsync(new SplitDefinitionRequest
        {
            Name = "Marketplace 70/30",
            Currency = "USD",
            Rules = new List<SplitRule>
            {
                new() { SubAccountReference = "acct_seller", ShareType = SplitShareType.Percentage, Percentage = 70m },
                new() { SubAccountReference = "acct_platform", ShareType = SplitShareType.Percentage, Percentage = 30m }
            }
        });
        var fetched = await provider.GetSplitAsync(split.Reference);
        Assert.NotNull(fetched);
        Assert.Equal(2, fetched!.Rules.Count);
    }

    [Fact]
    public async Task GetSplitAsync_UnknownReference_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var provider = Create(handler);
        Assert.Null(await provider.GetSplitAsync("split_unknown"));
    }

    [Fact]
    public async Task ChargeWithSplitAsync_SingleDestination_UsesTransferData()
    {
        var sawTransferData = false;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.Content is not null)
            {
                var body = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains("transfer_data", StringComparison.Ordinal)) sawTransferData = true;
            }
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"pi_split_1","object":"payment_intent","amount":10000,"currency":"usd","status":"succeeded"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ChargeWithSplitAsync(new ChargeWithSplitRequest
        {
            Payment = new PaymentRequest
            {
                PaymentMethodToken = "pm_x",
                Amount = 100m,
                Currency = "USD",
                Description = "split charge"
            },
            InlineRules = new List<SplitRule>
            {
                new() { SubAccountReference = "acct_dest", ShareType = SplitShareType.Percentage, Percentage = 100m }
            }
        });

        Assert.True(sawTransferData);
        Assert.Equal("pi_split_1", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
    }

    [Fact]
    public async Task ChargeWithSplitAsync_MultiDestination_IssuesTransfersAndReturnsGroup()
    {
        var transfers = 0;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("/transfers", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref transfers);
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"id":"tr_1","object":"transfer","amount":1000,"currency":"usd","destination":"acct_x"}
                    """);
            }
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"pi_split_2","object":"payment_intent","amount":10000,"currency":"usd","status":"succeeded","latest_charge":"ch_split_2"}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ChargeWithSplitAsync(new ChargeWithSplitRequest
        {
            Payment = new PaymentRequest
            {
                PaymentMethodToken = "pm_x",
                Amount = 100m,
                Currency = "USD",
                Description = "split charge"
            },
            InlineRules = new List<SplitRule>
            {
                new() { SubAccountReference = "acct_a", ShareType = SplitShareType.Percentage, Percentage = 60m },
                new() { SubAccountReference = "acct_b", ShareType = SplitShareType.Percentage, Percentage = 40m }
            }
        });

        Assert.Equal(2, transfers);
        Assert.Equal("pi_split_2", response.GatewayReference);
        Assert.Contains("transfer_group=", response.Message);
    }

    [Fact]
    public async Task ChargeWithSplitAsync_Throws4xxAsPaymentDeclined()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.PaymentRequired, """
            {"error":{"type":"card_error","code":"card_declined","message":"Declined"}}
            """));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ChargeWithSplitAsync(new ChargeWithSplitRequest
        {
            Payment = new PaymentRequest { PaymentMethodToken = "pm_x", Amount = 100m, Currency = "USD", Description = "x" },
            InlineRules = new List<SplitRule>
            {
                new() { SubAccountReference = "acct_dest", ShareType = SplitShareType.Percentage, Percentage = 100m }
            }
        }));
    }
}
