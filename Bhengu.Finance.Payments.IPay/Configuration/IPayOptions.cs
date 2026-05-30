// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.IPay.Configuration;

/// <summary>
/// Configuration for the iPay (Africa) provider. Bound from <c>Bhengu:Finance:Payments:IPay</c> in IConfiguration.
/// </summary>
public sealed class IPayOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:IPay";

    /// <summary>iPay vendor id (the merchant code issued at on-boarding).</summary>
    public string VendorId { get; set; } = string.Empty;

    /// <summary>iPay Hashkey used to compute HMAC-SHA256 over request fields.</summary>
    public string HashKey { get; set; } = string.Empty;

    /// <summary>"1" for live, "0" for sandbox/test. Defaults to "1".</summary>
    public string Live { get; set; } = "1";

    /// <summary>ISO 4217 default currency. Defaults to "KES".</summary>
    public string Currency { get; set; } = "KES";

    /// <summary>Merchant callback URL (the cbk field). iPay POSTs the payment outcome here.</summary>
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>When true, the SandboxUrl override is used. Defaults to false (live).</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the iPay base URL. Defaults to https://payments.ipayafrica.com/v3/ke.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Optional sandbox-mode base URL override.</summary>
    public string? SandboxUrl { get; set; }
}
