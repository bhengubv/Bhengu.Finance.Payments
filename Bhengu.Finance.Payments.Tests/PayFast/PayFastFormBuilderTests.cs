// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.PayFast.Builders;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayFast;

public class PayFastFormBuilderTests
{
    private static PayFastFormBuilder CreateBuilder() =>
        new(Options.Create(new PayFastOptions
        {
            MerchantId = "10000100",
            MerchantKey = "46f0cd694581a",
            Passphrase = "jt7NOE43FZPn",
            UseSandbox = true,
            ReturnUrl = "https://example.com/return",
            CancelUrl = "https://example.com/cancel",
            NotifyUrl = "https://example.com/notify"
        }), NullLogger<PayFastFormBuilder>.Instance);

    [Fact]
    public void BuildOnceOffPaymentUrl_IncludesAllRequiredFieldsAndSignature()
    {
        var url = CreateBuilder().BuildOnceOffPaymentUrl(
            mPaymentId: "test-payment-1",
            amount: 100.50m,
            itemName: "Test Item",
            description: "Hello",
            emailAddress: "buyer@example.com");

        Assert.StartsWith("https://sandbox.payfast.co.za/eng/process?", url);
        Assert.Contains("merchant_id=10000100", url);
        Assert.Contains("amount=100.50", url);
        Assert.Contains("item_name=Test+Item", url);
        Assert.Contains("email_address=buyer%40example.com", url);
        Assert.Contains("currency=ZAR", url);
        Assert.Contains("signature=", url);
    }

    [Fact]
    public void BuildSubscriptionUrl_FillsRecurringFieldsAndUsesValidFrequency()
    {
        var url = CreateBuilder().BuildSubscriptionUrl(
            mPaymentId: "sub-1",
            recurringAmount: 50m,
            frequency: 3,
            cycles: 12,
            itemName: "Monthly Plan");

        Assert.Contains("subscription_type=1", url);
        Assert.Contains("frequency=3", url);
        Assert.Contains("cycles=12", url);
        Assert.Contains("recurring_amount=50.00", url);
        Assert.Contains("item_name=Monthly+Plan", url);
        Assert.Contains("signature=", url);
    }

    [Fact]
    public void BuildSubscriptionUrl_DefaultsInvalidFrequencyToMonthly()
    {
        var url = CreateBuilder().BuildSubscriptionUrl(
            mPaymentId: "sub-1",
            recurringAmount: 10m,
            frequency: 99);

        Assert.Contains("frequency=3", url);
    }

    [Fact]
    public void BuildTokenisationUrl_UsesAdHocSubscriptionTypeAndZeroAmount()
    {
        var url = CreateBuilder().BuildTokenisationUrl(
            returnUrl: "https://example.com/return",
            cancelUrl: "https://example.com/cancel",
            notifyUrl: "https://example.com/notify");

        Assert.Contains("subscription_type=2", url);
        Assert.Contains("amount=0", url);
        Assert.Contains("signature=", url);
    }
}
