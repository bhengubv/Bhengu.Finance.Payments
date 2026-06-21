// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Cellulant.Configuration;
using Bhengu.Finance.Payments.Cellulant.Internals;
using Bhengu.Finance.Payments.Cellulant.Providers;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Marketplace;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Cellulant;

public class CellulantMarketplaceProviderTests
{
    private static StubHttpMessageHandler ComposeWithToken(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> businessHandler) =>
        new((req, ct) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("oauth/token", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"access_token":"tok_test","expires_in":3600}
                    """);
            return businessHandler(req, ct);
        });

    private static CellulantMarketplaceProvider Create(StubHttpMessageHandler handler, IBhenguDistributedCache? cache = null)
    {
        var opts = new CellulantOptions
        {
            ServiceCode = "TGNTEST",
            ApiKey = "apikey-test",
            ClientId = "client-1",
            ClientSecret = "secret-1",
            UseSandbox = true
        };
        var http = new HttpClient(handler);
        var optsInst = Options.Create(opts);
        var broker = new CellulantTokenBroker(optsInst, NullLogger<CellulantTokenBroker>.Instance);
        return new CellulantMarketplaceProvider(http, optsInst, NullLogger<CellulantMarketplaceProvider>.Instance, broker, cache);
    }

    [Fact]
    public async Task CreateSubAccountAsync_ReturnsSubAccount_OnSuccess()
    {
        var handler = ComposeWithToken((req, _) =>
        {
            Assert.Contains("sub-services", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"data":{"subServiceCode":"SUB-1","businessName":"Bobs","contactEmail":"bob@x.com","settlementAccount":"254700111222","isActive":true}}
                """);
        });
        var provider = Create(handler);
        var sub = await provider.CreateSubAccountAsync(new SubAccountRequest
        {
            BusinessName = "Bobs",
            ContactEmail = "bob@x.com",
            Country = "KE",
            SettlementAccountToken = "254700111222"
        });
        Assert.Equal("SUB-1", sub.Reference);
        Assert.True(sub.IsActive);
        Assert.Null(sub.OnboardingUrl);
    }

    [Fact]
    public async Task CreateSubAccountAsync_Throws_WhenSettlementAccountMissing()
    {
        var handler = ComposeWithToken((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.CreateSubAccountAsync(new SubAccountRequest
        {
            BusinessName = "Bob",
            ContactEmail = "b@x.com",
            Country = "KE"
        }));
    }

    [Fact]
    public async Task CreateSplitAsync_ReturnsLocalSplit()
    {
        var handler = ComposeWithToken((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        var split = await provider.CreateSplitAsync(new SplitDefinitionRequest
        {
            Name = "70/30",
            Currency = "KES",
            Rules =
            [
                new SplitRule { SubAccountReference = "SUB-1", ShareType = SplitShareType.Percentage, Percentage = 70m },
                new SplitRule { SubAccountReference = "SUB-2", ShareType = SplitShareType.Percentage, Percentage = 30m }
            ]
        });
        Assert.StartsWith("tingg-split-", split.Reference);
        Assert.Equal(2, split.Rules.Count);

        var rehydrated = await provider.GetSplitAsync(split.Reference);
        Assert.NotNull(rehydrated);
        Assert.Equal(split.Reference, rehydrated!.Reference);
    }

    [Fact]
    public async Task ChargeWithSplitAsync_ReturnsResponse_FromInlineRules()
    {
        // Verified: splits ride on the express-request call as `charge_beneficiaries`; response is the
        // status/results envelope. Source: https://docs.tingg.africa/docs/checkout-v3-express-checkout
        var handler = ComposeWithToken((req, _) =>
        {
            Assert.Contains("v3/checkout-api/checkout-request/express-request", req.RequestUri!.PathAndQuery);
            Assert.True(req.Headers.Contains("apiKey"), "apiKey header must be present on split charge");
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":{"status_code":200,"status_description":"success"},"results":{"short_url":"https://pay.tingg/abc","long_url":"https://pay.tingg/abc?x=1"}}
                """);
        });
        var provider = Create(handler);
        var response = await provider.ChargeWithSplitAsync(new ChargeWithSplitRequest
        {
            Payment = new PaymentRequest
            {
                PaymentMethodToken = "254700000000",
                Amount = 100m,
                Currency = "KES",
                Description = "split test"
            },
            InlineRules =
            [
                new SplitRule { SubAccountReference = "SUB-1", ShareType = SplitShareType.Percentage, Percentage = 100m }
            ]
        });

        Assert.False(string.IsNullOrEmpty(response.GatewayReference));
        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.Equal("https://pay.tingg/abc", response.RedirectUrl);
    }

    [Fact]
    public async Task ChargeWithSplitAsync_Throws_WhenSplitMissing()
    {
        var handler = ComposeWithToken((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.ChargeWithSplitAsync(new ChargeWithSplitRequest
        {
            Payment = new PaymentRequest
            {
                PaymentMethodToken = "254700000000",
                Amount = 10m,
                Currency = "KES",
                Description = "x"
            }
        }));
    }

    [Fact]
    public async Task CreateSubAccountAsync_Throws4xxAsPaymentDeclinedException()
    {
        var handler = ComposeWithToken((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.CreateSubAccountAsync(new SubAccountRequest
        {
            BusinessName = "Bob",
            ContactEmail = "b@x.com",
            Country = "KE",
            SettlementAccountToken = "254"
        }));
    }
}
