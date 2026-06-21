// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.ChipperCash.Configuration;
using Bhengu.Finance.Payments.ChipperCash.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.ChipperCash;

/// <summary>Chipper Cash tokenisation is a reserved scaffold (gated Network API) — it throws on use.</summary>
public class ChipperCashTokenisationProviderTests
{
    private static ChipperCashTokenisationProvider CreateRead() => new(
        new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
        Options.Create(new ChipperCashOptions { ApiKey = "k", ApiSecret = "s" }),
        NullLogger<ChipperCashTokenisationProvider>.Instance);

    private static ChipperCashRawCardTokenisationProvider CreateWrite() => new(
        new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
        Options.Create(new ChipperCashOptions { ApiKey = "k", ApiSecret = "s" }),
        NullLogger<ChipperCashRawCardTokenisationProvider>.Instance);

    [Fact]
    public async Task GetPaymentMethodAsync_Throws_PendingOnboarding() =>
        await Assert.ThrowsAsync<BhenguPaymentException>(() => CreateRead().GetPaymentMethodAsync("tok"));

    [Fact]
    public async Task DeletePaymentMethodAsync_Throws_PendingOnboarding() =>
        await Assert.ThrowsAsync<BhenguPaymentException>(() => CreateRead().DeletePaymentMethodAsync("tok"));

    [Fact]
    public void ListPaymentMethodsAsync_Throws_PendingOnboarding() =>
        Assert.Throws<BhenguPaymentException>(() => CreateRead().ListPaymentMethodsAsync("cust"));

    [Fact]
    public async Task TokeniseAsync_Throws_PendingOnboarding() =>
        await Assert.ThrowsAsync<BhenguPaymentException>(() => CreateWrite().TokeniseAsync(null!));
}
