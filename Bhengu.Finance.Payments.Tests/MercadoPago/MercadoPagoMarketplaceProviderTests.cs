// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Marketplace;
using Bhengu.Finance.Payments.MercadoPago.Configuration;
using Bhengu.Finance.Payments.MercadoPago.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.MercadoPago;

public class MercadoPagoMarketplaceProviderTests
{
    private static MercadoPagoMarketplaceProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new MercadoPagoOptions { AccessToken = "APP_USR-test" }),
            NullLogger<MercadoPagoMarketplaceProvider>.Instance);

    [Fact]
    public async Task CreateSubAccountAsync_PostsAccountEndpoint_AndReturnsSubAccount()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v1/accounts", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"seller_1","email":"seller@example.com","status":"active","onboarding_url":"https://mp.example.com/onboard","additional_info":{"business_name":"Acme"}}
                """);
        });
        var provider = Create(handler);
        var account = await provider.CreateSubAccountAsync(new SubAccountRequest
        {
            BusinessName = "Acme",
            ContactEmail = "seller@example.com",
            Country = "BR"
        });
        Assert.Equal("seller_1", account.Reference);
        Assert.Equal("Acme", account.BusinessName);
        Assert.True(account.IsActive);
        Assert.Equal("https://mp.example.com/onboard", account.OnboardingUrl);
    }

    [Fact]
    public async Task GetSubAccountAsync_DeserialisesSingle()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"seller_1","email":"seller@example.com","status":"active","additional_info":{"business_name":"Acme"}}
                """));
        var provider = Create(handler);
        var account = await provider.GetSubAccountAsync("seller_1");
        Assert.NotNull(account);
        Assert.Equal("seller_1", account!.Reference);
        Assert.True(account.IsActive);
    }

    [Fact]
    public async Task GetSubAccountAsync_ReturnsNull_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.Null(await provider.GetSubAccountAsync("nope"));
    }

    [Fact]
    public async Task CreateSplitAsync_CachesAndReturnsReference()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        var split = await provider.CreateSplitAsync(new SplitDefinitionRequest
        {
            Name = "70-30",
            Currency = "BRL",
            Rules =
            [
                new SplitRule { SubAccountReference = "seller_1", ShareType = SplitShareType.Percentage, Percentage = 70m },
                new SplitRule { SubAccountReference = "platform", ShareType = SplitShareType.Percentage, Percentage = 30m }
            ]
        });
        Assert.StartsWith("mp_split_", split.Reference);
        Assert.Equal(2, split.Rules.Count);

        var fetched = await provider.GetSplitAsync(split.Reference);
        Assert.NotNull(fetched);
        Assert.Equal(split.Reference, fetched!.Reference);
    }

    [Fact]
    public async Task ChargeWithSplitAsync_PostsPaymentWithApplicationFee()
    {
        string? body = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v1/payments", req.RequestUri!.PathAndQuery);
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":12345,"status":"approved","status_detail":"accredited"}
                """);
        });
        var provider = Create(handler);
        var result = await provider.ChargeWithSplitAsync(new ChargeWithSplitRequest
        {
            Payment = new PaymentRequest
            {
                PaymentMethodToken = "tok_card",
                Amount = 100m,
                Currency = "BRL",
                Description = "Marketplace charge",
                Metadata = new Dictionary<string, string> { ["payer_email"] = "buyer@example.com", ["payment_method_id"] = "visa" }
            },
            InlineRules =
            [
                new SplitRule { SubAccountReference = "seller_1", ShareType = SplitShareType.Percentage, Percentage = 70m },
                new SplitRule { SubAccountReference = "platform", ShareType = SplitShareType.Percentage, Percentage = 30m }
            ]
        });

        Assert.Equal("12345", result.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, result.Status);
        Assert.NotNull(body);
        Assert.Contains("\"application_fee\":30", body!);
        Assert.Contains("\"collector_id\":\"seller_1\"", body);
    }

    [Fact]
    public async Task ChargeWithSplitAsync_ThrowsWhenSplitMissing()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.ChargeWithSplitAsync(new ChargeWithSplitRequest
        {
            Payment = new PaymentRequest
            {
                PaymentMethodToken = "t",
                Amount = 10m,
                Currency = "BRL",
                Description = "x",
                Metadata = new Dictionary<string, string> { ["payer_email"] = "buyer@example.com" }
            }
        }));
    }

    [Fact]
    public async Task ChargeWithSplitAsync_ThrowsWhenNoSeller()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.ChargeWithSplitAsync(new ChargeWithSplitRequest
        {
            Payment = new PaymentRequest
            {
                PaymentMethodToken = "t",
                Amount = 10m,
                Currency = "BRL",
                Description = "x",
                Metadata = new Dictionary<string, string> { ["payer_email"] = "buyer@example.com" }
            },
            InlineRules = [new SplitRule { SubAccountReference = "platform", ShareType = SplitShareType.Percentage, Percentage = 100m }]
        }));
    }

    [Fact]
    public async Task ListSubAccountsAsync_ReturnsEmpty()
    {
        var provider = Create(new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("Should not POST")));
        var list = await provider.ListSubAccountsAsync().ToListAsync();
        Assert.Empty(list);
    }
}
