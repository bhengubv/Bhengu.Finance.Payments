// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.JamboPay.Configuration;
using Bhengu.Finance.Payments.JamboPay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.JamboPay;

/// <summary>
/// JamboPay is a reserved scaffold — its public API docs are offline, so the provider throws on use
/// rather than shipping a guessed wire format.
/// </summary>
public class JamboPayPaymentProviderTests
{
    private static JamboPayPaymentProvider Create() => new(
        new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
        Options.Create(new JamboPayOptions { ApiKey = "k", ClientId = "c", ClientSecret = "s", MerchantCode = "M" }),
        NullLogger<JamboPayPaymentProvider>.Instance);

    [Fact]
    public void ProviderName_IsJamboPay() => Assert.Equal("jambopay", Create().ProviderName);

    [Fact]
    public async Task ProcessPaymentAsync_Throws_Unavailable()
    {
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            Create().ProcessPaymentAsync(new PaymentRequest { PaymentMethodToken = "x", Amount = 100m, Currency = "KES", Description = "d" }));
        Assert.Contains("offline", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessRefundAsync_Throws_Unavailable() =>
        await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            Create().ProcessRefundAsync(new RefundRequest { GatewayReference = "x", Amount = 10m, Reason = "r" }));

    [Fact]
    public async Task ProcessPayoutAsync_Throws_Unavailable() =>
        await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            Create().ProcessPayoutAsync(new PayoutRequest { DestinationToken = "msisdn:254700000000", Amount = 10m, Currency = "KES", Description = "d" }));

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse() => Assert.False(Create().VerifyWebhookSignature("payload", "sig"));

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull() => Assert.Null(await Create().ParseWebhookAsync("anything"));
}
