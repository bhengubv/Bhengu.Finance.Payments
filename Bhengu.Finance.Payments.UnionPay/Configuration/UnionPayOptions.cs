// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.UnionPay.Configuration;

/// <summary>
/// Configuration for the China UnionPay (UPOP 5.1) provider.
/// Bound from <c>Bhengu:Finance:Payments:UnionPay</c> in IConfiguration.
/// </summary>
public sealed class UnionPayOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:UnionPay";

    /// <summary>UnionPay merchant id (merId) — 15 digits.</summary>
    public string MerId { get; set; } = string.Empty;

    /// <summary>Certificate id of the signing cert (certId). Sent as a request parameter.</summary>
    public string CertId { get; set; } = string.Empty;

    /// <summary>
    /// PEM-encoded RSA private key used to sign outbound requests.
    /// Either the raw "-----BEGIN PRIVATE KEY-----" PEM or just the base64 body — both are accepted.
    /// </summary>
    public string SignCertPrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// PEM-encoded UnionPay verification certificate (or public key) used to verify response signatures.
    /// Either the raw "-----BEGIN CERTIFICATE-----" / "-----BEGIN PUBLIC KEY-----" PEM or the base64 body.
    /// </summary>
    public string VerifyCertPublicKey { get; set; } = string.Empty;

    /// <summary>Customer-redirect URL UnionPay returns the user to after payment.</summary>
    public string FrontUrl { get; set; } = string.Empty;

    /// <summary>Async back-notify URL UnionPay POSTs settlement results to.</summary>
    public string BackUrl { get; set; } = string.Empty;

    /// <summary>UnionPay currency code. Defaults to 156 (CNY); use 840 for USD.</summary>
    public string Currency { get; set; } = "156";

    /// <summary>Character encoding parameter. Defaults to UTF-8.</summary>
    public string Encoding { get; set; } = "UTF-8";

    /// <summary>When true, the SDK targets the UnionPay sandbox.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the production base URL. Leave null to use https://gateway.95516.com.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Override the sandbox base URL. Leave null to use https://gateway.test.95516.com.</summary>
    public string? SandboxUrl { get; set; }
}
