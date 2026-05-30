// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.TymeBank.Configuration;

/// <summary>
/// Configuration for the TymeBank provider. Bound from <c>Bhengu:Finance:Payments:TymeBank</c>
/// in IConfiguration. TymeBank uses OAuth2 client_credentials for API auth and HMAC-SHA256
/// in <c>X-Tyme-Signature</c> for webhook authenticity.
/// </summary>
public sealed class TymeBankOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:TymeBank";

    /// <summary>OAuth2 client_id for the merchant's TymeBank developer integration.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OAuth2 client_secret companion to <see cref="ClientId"/>.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Merchant identifier for QR generation and reference correlation.</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>Webhook signing secret (HMAC-SHA256 in <c>X-Tyme-Signature</c>).</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Callback URL for payment lifecycle webhooks.</summary>
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>Default ISO 4217 currency code. Defaults to "ZAR".</summary>
    public string Currency { get; set; } = "ZAR";

    /// <summary>If true, requests are routed to <see cref="SandboxUrl"/> instead of <see cref="BaseUrl"/>.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Production base URL (default https://api.tymebank.co.za).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Sandbox base URL (default https://api-sandbox.tymebank.co.za).</summary>
    public string? SandboxUrl { get; set; }
}
