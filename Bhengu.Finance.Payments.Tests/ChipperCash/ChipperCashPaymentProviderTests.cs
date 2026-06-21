// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.ChipperCash.Configuration;
using Bhengu.Finance.Payments.ChipperCash.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.ChipperCash;

/// <summary>
/// Chipper Cash is a reserved scaffold — its Network API spec is gated to onboarded merchants, so the
/// provider throws on use rather than shipping a guessed wire format.
/// </summary>
public class ChipperCashPaymentProviderTests
{
    private static ChipperCashPaymentProvider Create() => new(
        new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
        Options.Create(new ChipperCashOptions { ApiKey = "k", ApiSecret = "s" }),
        NullLogger<ChipperCashPaymentProvider>.Instance);

    [Fact]
    public void ProviderName_IsChipperCash() => Assert.Equal("chippercash", Create().ProviderName);

    [Fact]
    public async Task ProcessPaymentAsync_Throws_PendingOnboarding()
    {
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            Create().ProcessPaymentAsync(new PaymentRequest { PaymentMethodToken = "x", Amount = 100m, Currency = "NGN", Description = "d" }));
        Assert.Contains("onboarding", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessRefundAsync_Throws_PendingOnboarding() =>
        await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            Create().ProcessRefundAsync(new RefundRequest { GatewayReference = "x", Amount = 10m, Reason = "r" }));

    [Fact]
    public async Task ProcessPayoutAsync_Throws_PendingOnboarding() =>
        await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            Create().ProcessPayoutAsync(new PayoutRequest { DestinationToken = "x", Amount = 10m, Currency = "NGN", Description = "d" }));

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse() => Assert.False(Create().VerifyWebhookSignature("payload", "sig"));

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull() => Assert.Null(await Create().ParseWebhookAsync("anything"));
}
