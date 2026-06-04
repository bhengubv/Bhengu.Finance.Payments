// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Marketplace;
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Bhengu.Finance.Payments.Flutterwave.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Flutterwave;

public class FlutterwaveMarketplaceProviderTests
{
    private static FlutterwaveMarketplaceProvider Create(StubHttpMessageHandler handler, FlutterwaveOptions? opts = null)
    {
        opts ??= new FlutterwaveOptions
        {
            SecretKey = "FLWSECK_TEST-xxx",
            RedirectUrl = "https://example.com/return"
        };
        var http = new HttpClient(handler);
        return new FlutterwaveMarketplaceProvider(http, Options.Create(opts), NullLogger<FlutterwaveMarketplaceProvider>.Instance);
    }

    [Fact]
    public async Task CreateSubAccountAsync_ReturnsSubAccount_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v3/subaccounts", req.RequestUri!.PathAndQuery);
            Assert.Equal(HttpMethod.Post, req.Method);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","message":"Subaccount created","data":{"id":1,"subaccount_id":"RS_ABC","business_name":"Vendor A","business_email":"vendor@example.com","account_bank":"044","account_number":"0690000040","status":"active","active":true}}
                """);
        });
        var provider = Create(handler);

        var sub = await provider.CreateSubAccountAsync(new SubAccountRequest
        {
            BusinessName = "Vendor A",
            ContactEmail = "vendor@example.com",
            Country = "NG",
            SettlementAccountToken = "044:0690000040"
        });

        Assert.Equal("RS_ABC", sub.Reference);
        Assert.Equal("Vendor A", sub.BusinessName);
        Assert.True(sub.IsActive);
    }

    [Fact]
    public async Task CreateSubAccountAsync_Throws_WhenSettlementAccountTokenMissing()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.CreateSubAccountAsync(new SubAccountRequest
        {
            BusinessName = "x", ContactEmail = "x@x.com", Country = "NG"
        }));
    }

    [Fact]
    public async Task CreateSubAccountAsync_Throws_WhenSettlementAccountTokenMalformed()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.CreateSubAccountAsync(new SubAccountRequest
        {
            BusinessName = "x", ContactEmail = "x@x.com", Country = "NG", SettlementAccountToken = "not-a-bank-account"
        }));
        Assert.Equal("invalid_destination", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task GetSubAccountAsync_ReturnsNull_When404()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "no such subaccount"));
        var provider = Create(handler);
        Assert.Null(await provider.GetSubAccountAsync("RS_NONE"));
    }

    [Fact]
    public async Task ListSubAccountsAsync_ReturnsList_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"status":"success","data":[
                {"id":1,"subaccount_id":"RS_A","business_name":"Vendor A","status":"active","active":true},
                {"id":2,"subaccount_id":"RS_B","business_name":"Vendor B","status":"inactive","active":false}
            ]}
            """));
        var provider = Create(handler);
        var list = await provider.ListSubAccountsAsync().ToListAsync();
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task CreateSplitAsync_And_GetSplitAsync_RoundTrip()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));

        var def = await provider.CreateSplitAsync(new SplitDefinitionRequest
        {
            Name = "60/40",
            Currency = "NGN",
            Rules = new[]
            {
                new SplitRule { SubAccountReference = "RS_A", ShareType = SplitShareType.Percentage, Percentage = 60m },
                new SplitRule { SubAccountReference = "RS_B", ShareType = SplitShareType.Percentage, Percentage = 40m }
            }
        });

        Assert.StartsWith("split-", def.Reference);
        Assert.Equal(2, def.Rules.Count);

        var fetched = await provider.GetSplitAsync(def.Reference);
        Assert.NotNull(fetched);
        Assert.Equal(def.Reference, fetched!.Reference);
    }

    [Fact]
    public async Task GetSplitAsync_ReturnsNull_WhenUnknown()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Null(await provider.GetSplitAsync("unknown-ref"));
    }

    [Fact]
    public async Task ChargeWithSplitAsync_InlineRules_PostsSubaccounts()
    {
        var captured = string.Empty;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            captured = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","message":"Hosted Link","data":{"link":"https://checkout.flutterwave.com/v3/hosted/pay/x"}}
                """);
        });
        var provider = Create(handler);

        var response = await provider.ChargeWithSplitAsync(new ChargeWithSplitRequest
        {
            Payment = new PaymentRequest
            {
                PaymentMethodToken = "tx-1",
                Amount = 100m,
                Currency = "NGN",
                Description = "Split charge",
                Metadata = new Dictionary<string, string> { ["email"] = "buyer@example.com" }
            },
            InlineRules = new[]
            {
                new SplitRule { SubAccountReference = "RS_A", ShareType = SplitShareType.Percentage, Percentage = 70m },
                new SplitRule { SubAccountReference = "RS_B", ShareType = SplitShareType.Percentage, Percentage = 30m }
            }
        });

        Assert.Equal("tx-1", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Contains("RS_A", captured);
        Assert.Contains("subaccounts", captured);
    }

    [Fact]
    public async Task ChargeWithSplitAsync_Throws_WhenNeitherReferenceNorInlineProvided()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.ChargeWithSplitAsync(new ChargeWithSplitRequest
        {
            Payment = new PaymentRequest
            {
                PaymentMethodToken = "tx-1", Amount = 100m, Currency = "NGN", Description = "x",
                Metadata = new Dictionary<string, string> { ["email"] = "x@x.com" }
            }
        }));
    }
}
