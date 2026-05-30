// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Razorpay.Configuration;

/// <summary>
/// Configuration for the Razorpay (India) provider. Bound from <c>Bhengu:Finance:Payments:Razorpay</c>
/// in IConfiguration.
/// </summary>
public sealed class RazorpayOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Razorpay";

    /// <summary>Razorpay Key ID. Used as the Basic-auth username on every request.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>Razorpay Key Secret. Used as the Basic-auth password on every request.</summary>
    public string KeySecret { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256 secret used to verify the <c>X-Razorpay-Signature</c> webhook header.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>RazorpayX virtual account number, the funding source for payouts. Required for IPayoutProvider.</summary>
    public string RazorpayXAccountNumber { get; set; } = string.Empty;

    /// <summary>ISO 4217 default currency. Defaults to "INR".</summary>
    public string Currency { get; set; } = "INR";

    /// <summary>When true, sandbox/test credentials are assumed; the SDK still uses the live base URL
    /// because Razorpay test keys route to the same host (api.razorpay.com).</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the Razorpay base URL. Defaults to https://api.razorpay.com/.</summary>
    public string? BaseUrl { get; set; }
}
