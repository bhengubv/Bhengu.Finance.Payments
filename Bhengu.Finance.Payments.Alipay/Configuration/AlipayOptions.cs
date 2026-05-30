// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Alipay.Configuration;

/// <summary>
/// Configuration for the Alipay+ Cross-Border provider.
/// Bound from <c>Bhengu:Finance:Payments:Alipay</c> in IConfiguration.
/// </summary>
public sealed class AlipayOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Alipay";

    /// <summary>Alipay client identifier — assigned per merchant when onboarded with Ant Group.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// PEM-encoded RSA private key used to sign outbound requests.
    /// Either the raw "-----BEGIN PRIVATE KEY-----" PEM or just the base64 body — both are accepted.
    /// </summary>
    public string MerchantPrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// PEM-encoded Alipay public key used to verify response and webhook signatures.
    /// Either the raw "-----BEGIN PUBLIC KEY-----" PEM or just the base64 body — both are accepted.
    /// </summary>
    public string AlipayPublicKey { get; set; } = string.Empty;

    /// <summary>Async payment notification (webhook) URL Alipay will POST settlement updates to.</summary>
    public string NotifyUrl { get; set; } = string.Empty;

    /// <summary>URL the cashier returns the customer to after payment completion.</summary>
    public string RedirectUrl { get; set; } = string.Empty;

    /// <summary>ISO 4217 currency used when the caller does not override per-request. Defaults to USD for cross-border.</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>When true, the SDK targets the Alipay+ sandbox.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the production base URL. Leave null to use https://open-global.alipay.com.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Override the sandbox base URL. Leave null to use https://open-global.alipay.com/api/sandbox.</summary>
    public string? SandboxUrl { get; set; }
}
