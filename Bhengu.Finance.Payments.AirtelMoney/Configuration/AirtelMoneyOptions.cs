// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.AirtelMoney.Configuration;

/// <summary>
/// Configuration for the Airtel Money provider.
/// Bound from <c>Bhengu:Finance:Payments:AirtelMoney</c> in IConfiguration.
/// </summary>
public sealed class AirtelMoneyOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:AirtelMoney";

    /// <summary>Client ID issued by the Airtel Africa developer portal.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Client Secret issued alongside <see cref="ClientId"/>.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>ISO 3166-1 alpha-2 country code (e.g. <c>KE</c>, <c>UG</c>, <c>TZ</c>). Sent as <c>X-Country</c>.</summary>
    public string Country { get; set; } = "KE";

    /// <summary>ISO 4217 currency code for the target country (e.g. <c>KES</c>, <c>UGX</c>, <c>TZS</c>). Sent as <c>X-Currency</c>.</summary>
    public string Currency { get; set; } = "KES";

    /// <summary>Public HTTPS URL Airtel Money POSTs callbacks to.</summary>
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256 secret used to verify the <c>signature</c> field on inbound callbacks.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// For Disbursements: the initiator PIN encrypted with Airtel's RSA public key and Base64-encoded.
    /// Must be pre-computed by the caller and rotated whenever the merchant PIN changes. UAT/sandbox
    /// permits an empty value; production rejects empty PINs with status code <c>DP00800001003</c>.
    /// </summary>
    public string? EncryptedDisbursementPin { get; set; }

    /// <summary>Use the sandbox base URL when true.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the base URL. Defaults to https://openapiuat.airtel.africa/ (sandbox) or https://openapi.airtel.africa/ (production).</summary>
    public string? BaseUrl { get; set; }
}
