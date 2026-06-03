// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Marketplace;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Bhengu.Finance.Payments.Razorpay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Razorpay;

public class RazorpayMarketplaceProviderTests
{
    private static RazorpayMarketplaceProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new RazorpayOptions { KeyId = "rzp_test_x", KeySecret = "secret_x" }),
            NullLogger<RazorpayMarketplaceProvider>.Instance);

    [Fact]
    public async Task CreateSubAccountAsync_PostsToV2Accounts_AndReturnsSubAccount()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("v2/accounts", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"acc_abc","type":"linked","status":"activated","email":"vendor@example.com","legal_business_name":"Vendor Inc","activation_form_milestone":null}
                """);
        });
        var provider = Create(handler);
        var account = await provider.CreateSubAccountAsync(new SubAccountRequest
        {
            BusinessName = "Vendor Inc",
            ContactEmail = "vendor@example.com",
            Country = "IN"
        });

        Assert.Equal("acc_abc", account.Reference);
        Assert.True(account.IsActive);
        Assert.Equal("Vendor Inc", account.BusinessName);
    }

    [Fact]
    public async Task GetSubAccountAsync_ReturnsNull_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.Null(await provider.GetSubAccountAsync("acc_missing"));
    }

    [Fact]
    public async Task ListSubAccountsAsync_ReturnsEmpty_ByDesign()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("Should not be called"));
        var provider = Create(handler);
        var list = await provider.ListSubAccountsAsync();
        Assert.Empty(list);
    }

    [Fact]
    public async Task CreateSplitAsync_CachesAndReturns()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("Should not call HTTP"));
        var provider = Create(handler);
        var rules = new[] { new SplitRule { SubAccountReference = "acc_1", ShareType = SplitShareType.Percentage, Percentage = 80m } };
        var split = await provider.CreateSplitAsync(new SplitDefinitionRequest
        {
            Name = "80/20",
            Currency = "INR",
            Rules = rules
        });

        Assert.NotEmpty(split.Reference);
        Assert.Equal("80/20", split.Name);
        Assert.Single(split.Rules);

        var fetched = await provider.GetSplitAsync(split.Reference);
        Assert.NotNull(fetched);
        Assert.Equal(split.Reference, fetched!.Reference);
    }

    [Fact]
    public async Task GetSplitAsync_ReturnsNull_ForUnknown()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("Should not call HTTP"));
        var provider = Create(handler);
        Assert.Null(await provider.GetSplitAsync("split_local_unknown"));
    }

    [Fact]
    public async Task ChargeWithSplitAsync_CapturesThenTransfers()
    {
        var calls = new List<string>();
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            calls.Add(req.RequestUri!.PathAndQuery);
            if (req.RequestUri!.PathAndQuery.Contains("/capture", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                    """{"id":"pay_z","entity":"payment","amount":100000,"currency":"INR","status":"captured"}""");
            if (req.RequestUri!.PathAndQuery.Contains("/transfers", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                    """{"entity":"collection","count":2,"items":[{"id":"trf_1"},{"id":"trf_2"}]}""");
            throw new InvalidOperationException($"Unexpected: {req.RequestUri}");
        });
        var provider = Create(handler);
        var response = await provider.ChargeWithSplitAsync(new ChargeWithSplitRequest
        {
            Payment = new PaymentRequest
            {
                PaymentMethodToken = "pay_z",
                Amount = 1000m,
                Currency = "INR",
                Description = "Test"
            },
            InlineRules = new[]
            {
                new SplitRule { SubAccountReference = "acc_1", ShareType = SplitShareType.Percentage, Percentage = 70m },
                new SplitRule { SubAccountReference = "acc_2", ShareType = SplitShareType.Percentage, Percentage = 30m }
            }
        });

        Assert.Equal("pay_z", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Equal(2, calls.Count);
        Assert.Contains("/capture", calls[0]);
        Assert.Contains("/transfers", calls[1]);
    }

    [Fact]
    public async Task ChargeWithSplitAsync_Throws_WhenNoRules()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("Should not call HTTP"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.ChargeWithSplitAsync(new ChargeWithSplitRequest
        {
            Payment = new PaymentRequest { PaymentMethodToken = "x", Amount = 100m, Currency = "INR", Description = "x" }
        }));
    }

    [Fact]
    public async Task CreateSubAccountAsync_OnRateLimit_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "throttled"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.CreateSubAccountAsync(new SubAccountRequest
        {
            BusinessName = "X",
            ContactEmail = "x@example.com",
            Country = "IN"
        }));
    }
}
