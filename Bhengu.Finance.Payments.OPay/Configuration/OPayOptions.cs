// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.OPay.Configuration;

/// <summary>
/// Configuration for the OPay provider. Bound from <c>Bhengu:Finance:Payments:OPay</c>.
/// </summary>
public sealed class OPayOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:OPay";

    /// <summary>OPay public key (sent in request body).</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>
    /// OPay merchant Private Key. Used to sign server-to-server requests with HMAC-SHA512 (over the
    /// alphabetically key-sorted body) and to verify callbacks with HMAC-SHA3-512 (over OPay's fixed
    /// sign-content string). See https://documentation.opaycheckout.com/api-signature and
    /// https://documentation.opaycheckout.com/callback-signature.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Merchant id (the OPay "sn" short name) used in cashier requests.</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>ISO 3166-1 alpha-2 country code (default "NG"). OPay International supports NG/EG/PK.</summary>
    public string Country { get; set; } = "NG";

    /// <summary>Callback URL OPay will post webhook events to. Required by the cashier API.</summary>
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>Return URL the customer is sent to after completing the cashier flow.</summary>
    public string ReturnUrl { get; set; } = string.Empty;

    /// <summary>When true, requests target the sandbox URL instead of the production one.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the production base URL (defaults to https://liveapi.opaycheckout.com).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Override the sandbox/staging base URL (defaults to https://testapi.opaycheckout.com).</summary>
    public string? SandboxUrl { get; set; }
}
