// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Mukuru.Configuration;
using Bhengu.Finance.Payments.Mukuru.Providers;
using Bhengu.Finance.Payments.PayFast.Builders;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Bhengu.Finance.Payments.PayFast.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Mukuru;

/// <summary>
/// MukuruPay is a PayFast payment method — the Mukuru provider delegates to PayFast: a charge builds a
/// PayFast checkout redirect (where the buyer selects MukuruPay), and refund/webhook delegate to PayFast.
/// </summary>
public class MukuruPaymentProviderTests
{
    private static readonly IOptions<PayFastOptions> PayFast =
        Options.Create(new PayFastOptions { MerchantId = "10000100", MerchantKey = "46f0cd694581a", Passphrase = "pp", UseSandbox = true });

    private static MukuruPaymentProvider Create()
    {
        var formBuilder = new PayFastFormBuilder(PayFast, NullLogger<PayFastFormBuilder>.Instance);
        var payFast = new PayFastPaymentProvider(
            new HttpClient(new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"))),
            PayFast, NullLogger<PayFastPaymentProvider>.Instance);
        return new MukuruPaymentProvider(formBuilder, payFast, Options.Create(new MukuruOptions()), NullLogger<MukuruPaymentProvider>.Instance);
    }

    [Fact]
    public void ProviderName_IsMukuru() => Assert.Equal("mukuru", Create().ProviderName);

    [Fact]
    public async Task ProcessPaymentAsync_BuildsPayFastRedirect()
    {
        var response = await Create().ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "order-1", Amount = 250m, Currency = "ZAR", Description = "Cash order"
        });

        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.NotNull(response.RedirectUrl);
        Assert.Contains("payfast.co.za", response.RedirectUrl!);
        Assert.Contains("m_payment_id=order-1", response.RedirectUrl!);
    }

    [Fact]
    public async Task ProcessRefundAsync_DelegatesToPayFast_ThrowsInSandbox()
    {
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            Create().ProcessRefundAsync(new RefundRequest { GatewayReference = "PF-1", Amount = 50m, Reason = "x" }));
        Assert.Contains("sandbox", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
