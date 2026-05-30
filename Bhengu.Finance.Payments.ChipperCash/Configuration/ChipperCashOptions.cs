// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.ChipperCash.Configuration;

/// <summary>
/// Configuration for the Chipper Cash provider. Bound from <c>Bhengu:Finance:Payments:ChipperCash</c>.
/// </summary>
public sealed class ChipperCashOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:ChipperCash";

    /// <summary>Chipper Cash API key. Sent in the <c>Authorization</c> header on every request.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Chipper Cash API secret. Used to sign request bodies (HMAC-SHA256, lowercase hex) and verify webhooks.</summary>
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>Merchant id issued by Chipper Cash.</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>Callback URL Chipper Cash will post webhook events to.</summary>
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>ISO 3166-1 alpha-2 country code (default "NG"). Chipper operates across NG, GH, KE, UG, TZ, RW, ZA, USA.</summary>
    public string Country { get; set; } = "NG";

    /// <summary>ISO 4217 currency code (default "NGN"). Matches the Country setting in normal use.</summary>
    public string Currency { get; set; } = "NGN";

    /// <summary>When true, requests target a sandbox URL instead of the production one.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the Chipper Cash base URL. Leave null in normal use (defaults to https://api.chippercash.com/).</summary>
    public string? BaseUrl { get; set; }
}
