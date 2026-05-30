// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.WeChatPay.Configuration;

/// <summary>
/// Configuration for the WeChat Pay v3 provider.
/// Bound from <c>Bhengu:Finance:Payments:WeChatPay</c> in IConfiguration.
/// </summary>
public sealed class WeChatPayOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:WeChatPay";

    /// <summary>WeChat Open Platform AppId — required on every transaction.</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>WeChat Pay merchant identifier (mchid).</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>Serial number of the merchant API certificate. Sent in the Authorization header as <c>serial_no</c>.</summary>
    public string MerchantCertSerialNo { get; set; } = string.Empty;

    /// <summary>
    /// PEM-encoded RSA private key paired with the merchant API certificate.
    /// Either the raw "-----BEGIN PRIVATE KEY-----" PEM or just the base64 body — both are accepted.
    /// </summary>
    public string MerchantPrivateKey { get; set; } = string.Empty;

    /// <summary>32-character v3 API key used for AEAD-AES-256-GCM decryption of webhook resources.</summary>
    public string V3ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// PEM-encoded WeChat Pay platform public certificate used to verify response and webhook signatures.
    /// Either the raw "-----BEGIN CERTIFICATE-----" / "-----BEGIN PUBLIC KEY-----" PEM or just the base64 body.
    /// </summary>
    public string WeChatPayPlatformCertificate { get; set; } = string.Empty;

    /// <summary>Async notification (webhook) URL WeChat Pay will POST settlement updates to.</summary>
    public string NotifyUrl { get; set; } = string.Empty;

    /// <summary>ISO 4217 currency used per request. Defaults to CNY.</summary>
    public string Currency { get; set; } = "CNY";

    /// <summary>When true, the SDK targets the WeChat Pay sandbox.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the base URL. Leave null to use https://api.mch.weixin.qq.com.</summary>
    public string? BaseUrl { get; set; }
}
