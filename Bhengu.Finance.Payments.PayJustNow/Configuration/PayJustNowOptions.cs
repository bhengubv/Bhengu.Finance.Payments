// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.PayJustNow.Configuration;

/// <summary>
/// Configuration for the PayJustNow BNPL provider. Bound from <c>Bhengu:Finance:Payments:PayJustNow</c>.
/// </summary>
public sealed class PayJustNowOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:PayJustNow";

    /// <summary>
    /// PayJustNow Merchant API Key. Used as the HTTP Basic <b>password</b>
    /// (<c>Authorization: Basic base64(MerchantId:ApiKey)</c>). Maps to the WooCommerce gateway's
    /// "Merchant API Key" setting. Source: payjustnow.class.php L278–L283, L533/L725.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// PayJustNow Merchant ID. Used as the HTTP Basic <b>username</b>. Maps to the WooCommerce gateway's
    /// "Merchant ID" setting. Source: payjustnow.class.php L271–L276, L531/L723.
    /// </summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>If true, all requests go to sandbox.payjustnow.com instead of production.</summary>
    public bool UseSandbox { get; set; } = false;

    /// <summary>Override the production base URL. Leave null to use the default.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Override the sandbox base URL. Leave null to use the default.</summary>
    public string? SandboxUrl { get; set; }
}
