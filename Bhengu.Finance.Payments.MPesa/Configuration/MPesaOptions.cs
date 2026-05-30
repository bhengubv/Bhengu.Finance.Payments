// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.MPesa.Configuration;

/// <summary>
/// Configuration for the Safaricom M-Pesa (Daraja) provider.
/// Bound from <c>Bhengu:Finance:Payments:MPesa</c> in IConfiguration.
/// </summary>
public sealed class MPesaOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:MPesa";

    /// <summary>Daraja app Consumer Key (issued in the Safaricom Developer Portal).</summary>
    public string ConsumerKey { get; set; } = string.Empty;

    /// <summary>Daraja app Consumer Secret. Used with <see cref="ConsumerKey"/> for OAuth2 Basic auth.</summary>
    public string ConsumerSecret { get; set; } = string.Empty;

    /// <summary>The PayBill/Till business short code that receives the funds.</summary>
    public string BusinessShortCode { get; set; } = string.Empty;

    /// <summary>The Lipa Na M-Pesa Online passkey for STK Push. Combined with shortcode + timestamp to form Password.</summary>
    public string Passkey { get; set; } = string.Empty;

    /// <summary>Public HTTPS URL Safaricom POSTs the STK Push callback to.</summary>
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>
    /// Opaque token embedded in the callback URL path (e.g. <c>/mpesa/callback/{token}</c>).
    /// M-Pesa does NOT sign webhook payloads — verification relies on this unguessable URL token.
    /// </summary>
    public string CallbackUrlToken { get; set; } = string.Empty;

    /// <summary>For B2C payouts: shortcode initiating the payout.</summary>
    public string InitiatorName { get; set; } = string.Empty;

    /// <summary>For B2C payouts: encrypted security credential (Base64 of RSA-encrypted password using the Safaricom public cert).</summary>
    public string SecurityCredential { get; set; } = string.Empty;

    /// <summary>For B2C: the queue timeout URL Safaricom POSTs to on request timeout.</summary>
    public string QueueTimeoutUrl { get; set; } = string.Empty;

    /// <summary>For B2C: the result URL Safaricom POSTs the final outcome to.</summary>
    public string ResultUrl { get; set; } = string.Empty;

    /// <summary>Use the sandbox base URL when true.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the production base URL. Defaults to https://api.safaricom.co.ke/.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Override the sandbox base URL. Defaults to https://sandbox.safaricom.co.ke/.</summary>
    public string? SandboxUrl { get; set; }
}
