// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Slydepay.Configuration;

/// <summary>
/// Configuration for the Slydepay (Ghana) provider. Bound from <c>Bhengu:Finance:Payments:Slydepay</c> in IConfiguration.
/// </summary>
public sealed class SlydepayOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Slydepay";

    /// <summary>The merchant's Slydepay-registered email or mobile number.</summary>
    public string EmailOrMobile { get; set; } = string.Empty;

    /// <summary>The merchant key from the Slydepay merchant dashboard.</summary>
    public string MerchantKey { get; set; } = string.Empty;

    /// <summary>ISO 4217 default currency. Defaults to "GHS".</summary>
    public string Currency { get; set; } = "GHS";

    /// <summary>Payment-channel bitmask sent to ProcessPaymentOrder (1=card, 2=mobile, 4=wallet, 7=all). Defaults to "7".</summary>
    public string PaymentChannels { get; set; } = "7";

    /// <summary>The PaymentNotificationUrl. Slydepay POSTs a callback here when the customer completes payment.</summary>
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>When true, the SandboxUrl is used. Defaults to false.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the Slydepay live base URL. Defaults to https://app.slydepay.com.gh.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Override the Slydepay UAT base URL. Defaults to https://uat.slydepay.com.gh.</summary>
    public string? SandboxUrl { get; set; }
}
