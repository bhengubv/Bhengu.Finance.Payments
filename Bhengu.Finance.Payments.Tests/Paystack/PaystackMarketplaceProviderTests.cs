// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Marketplace;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Internals;
using Bhengu.Finance.Payments.Paystack.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paystack;

public class PaystackMarketplaceProviderTests
{
    private static PaystackMarketplaceProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new PaystackOptions { SecretKey = "sk_test_xx", DefaultEmail = "buyer@example.com" }),
            NullLogger<PaystackMarketplaceProvider>.Instance,
            new PaystackIdempotencyCache());

    [Fact]
    public async Task CreateSubAccountAsync_ReturnsSubAccount_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("subaccount", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"data":{"subaccount_code":"ACCT_1","business_name":"Shop","primary_contact_email":"shop@example.com","account_number":"0123456789","settlement_bank":"044","active":true}}
                """);
        });
        var provider = Create(handler);
        var sa = await provider.CreateSubAccountAsync(new SubAccountRequest
        {
            BusinessName = "Shop",
            ContactEmail = "shop@example.com",
            Country = "NG",
            SettlementAccountToken = "0123456789",
            Metadata = new Dictionary<string, string>
            {
                ["settlement_bank"] = "044",
                ["percentage_charge"] = "5"
            }
        });
        Assert.Equal("ACCT_1", sa.Reference);
        Assert.True(sa.IsActive);
        Assert.Null(sa.OnboardingUrl);
    }

    [Fact]
    public async Task CreateSubAccountAsync_Throws_WhenSettlementBankMissing()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.CreateSubAccountAsync(new SubAccountRequest
        {
            BusinessName = "Shop",
            ContactEmail = "shop@example.com",
            Country = "NG",
            SettlementAccountToken = "0123456789"
        }));
    }

    [Fact]
    public async Task GetSubAccountAsync_ReturnsNull_On404()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.Null(await provider.GetSubAccountAsync("ACCT_missing"));
    }

    [Fact]
    public async Task ListSubAccountsAsync_ReturnsMapped()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"status":true,"data":[{"subaccount_code":"A","business_name":"X","active":true},{"subaccount_code":"B","business_name":"Y","active":false}]}
            """));
        var provider = Create(handler);
        var list = await provider.ListSubAccountsAsync().ToListAsync();
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task CreateSplitAsync_PercentageSplit_BuildsValidPayload()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("split", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"data":{"name":"Marketplace split","split_code":"SPL_1","type":"percentage","currency":"NGN","bearer_type":"all-proportional","subaccounts":[
                    {"subaccount":"ACCT_1","share":70},
                    {"subaccount":"ACCT_2","share":30}
                ]}}
                """);
        });
        var provider = Create(handler);
        var split = await provider.CreateSplitAsync(new SplitDefinitionRequest
        {
            Name = "Marketplace split",
            Currency = "NGN",
            Rules =
            [
                new SplitRule { SubAccountReference = "ACCT_1", ShareType = SplitShareType.Percentage, Percentage = 70m },
                new SplitRule { SubAccountReference = "ACCT_2", ShareType = SplitShareType.Percentage, Percentage = 30m }
            ]
        });
        Assert.Equal("SPL_1", split.Reference);
        Assert.Equal(2, split.Rules.Count);
        Assert.Equal(SplitShareType.Percentage, split.Rules[0].ShareType);
    }

    [Fact]
    public async Task CreateSplitAsync_ThrowsOnMixedShareTypes()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":true,"data":{}}"""));
        var provider = Create(handler);
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.CreateSplitAsync(new SplitDefinitionRequest
        {
            Name = "Bad",
            Currency = "NGN",
            Rules =
            [
                new SplitRule { SubAccountReference = "A", ShareType = SplitShareType.Percentage, Percentage = 50m },
                new SplitRule { SubAccountReference = "B", ShareType = SplitShareType.FixedAmount, Amount = 1m }
            ]
        }));
    }

    [Fact]
    public async Task ChargeWithSplitAsync_PostsToChargeAuthorization()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("transaction/charge_authorization", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"message":"ok","data":{"reference":"ref_split_1","status":"success"}}
                """);
        });
        var provider = Create(handler);
        var resp = await provider.ChargeWithSplitAsync(new ChargeWithSplitRequest
        {
            Payment = new PaymentRequest
            {
                PaymentMethodToken = "AUTH_x",
                Amount = 150m,
                Currency = "NGN",
                Description = "split charge"
            },
            SplitReference = "SPL_1"
        });
        Assert.Equal(PaymentStatus.Completed, resp.Status);
        Assert.Equal("ref_split_1", resp.GatewayReference);
    }

    [Fact]
    public async Task ChargeWithSplitAsync_ThrowsWhenNeitherReferenceNorRulesProvided()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.ChargeWithSplitAsync(new ChargeWithSplitRequest
        {
            Payment = new PaymentRequest { PaymentMethodToken = "A", Amount = 1m, Currency = "NGN", Description = "x" }
        }));
    }
}
