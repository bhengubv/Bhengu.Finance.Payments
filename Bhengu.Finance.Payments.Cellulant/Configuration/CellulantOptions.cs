// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Cellulant.Configuration;

/// <summary>
/// Configuration for the Cellulant (Tingg / Mula) provider. Bound from
/// <c>Bhengu:Finance:Payments:Cellulant</c> in IConfiguration.
/// </summary>
public sealed class CellulantOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Cellulant";

    /// <summary>Tingg service code identifying the merchant service.</summary>
    public string ServiceCode { get; set; } = string.Empty;

    /// <summary>OAuth2 client ID used to mint access tokens.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OAuth2 client secret used to mint access tokens.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Merchant transaction identifier prefix used on outbound requests.</summary>
    public string? MerchantTransactionId { get; set; }

    /// <summary>Redirect URL Tingg sends the customer to after the checkout.</summary>
    public string? CallbackUrl { get; set; }

    /// <summary>HMAC-SHA256 webhook secret used to verify the <c>x-tingg-signature</c> header.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>ISO-3166 country code (e.g. "KE", "NG", "ZA"). Defaults to Kenya — Tingg's home market.</summary>
    public string CountryCode { get; set; } = "KE";

    /// <summary>Use the Tingg sandbox endpoint instead of production.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the Tingg base URL. Leave null to use the sandbox/production default.</summary>
    public string? BaseUrl { get; set; }
}
