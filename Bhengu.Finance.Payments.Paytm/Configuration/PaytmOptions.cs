// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Paytm.Configuration;

/// <summary>
/// Configuration for the Paytm (India) provider. Bound from <c>Bhengu:Finance:Payments:Paytm</c>
/// in IConfiguration.
/// </summary>
public sealed class PaytmOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Paytm";

    /// <summary>Paytm Merchant ID (MID). Identifies the merchant on every call.</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>Paytm Merchant Key. Used to derive the checksum on every signed call and webhook.</summary>
    public string MerchantKey { get; set; } = string.Empty;

    /// <summary>Paytm website name. "WEBSTAGING" in sandbox, "DEFAULT" (or a custom site name) in production.</summary>
    public string WebsiteName { get; set; } = "DEFAULT";

    /// <summary>Callback URL for the hosted checkout (S2S notification destination).</summary>
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>Industry type ID. Defaults to "Retail".</summary>
    public string Industry { get; set; } = "Retail";

    /// <summary>ISO 4217 default currency. Defaults to "INR".</summary>
    public string Currency { get; set; } = "INR";

    /// <summary>When true, the SandboxUrl override is used. Defaults to false (production securegw.paytm.in).</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the Paytm base URL. Defaults to https://securegw.paytm.in/.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Sandbox base URL override. Defaults to https://securegw-stage.paytm.in/.</summary>
    public string? SandboxUrl { get; set; }
}
