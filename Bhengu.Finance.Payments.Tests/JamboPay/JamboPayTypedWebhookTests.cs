// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.JamboPay.Configuration;
using Bhengu.Finance.Payments.JamboPay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.JamboPay;

/// <summary>JamboPay is a reserved scaffold — webhook parsing returns null (no live integration).</summary>
public class JamboPayTypedWebhookTests
{
    private static JamboPayPaymentProvider Create() => new(
        new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
        Options.Create(new JamboPayOptions { ApiKey = "k", ClientId = "c", ClientSecret = "s", MerchantCode = "M" }),
        NullLogger<JamboPayPaymentProvider>.Instance);

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForAnyEvent() =>
        Assert.Null(await Create().ParseWebhookAsync("""{"event":"payment.completed","transaction_ref":"R-1"}"""));

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson() =>
        Assert.Null(await Create().ParseWebhookAsync("not json"));
}
