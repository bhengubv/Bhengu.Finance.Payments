// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Flutterwave.Configuration;

/// <summary>
/// Configuration for the Flutterwave provider. Bound from
/// <c>Bhengu:Finance:Payments:Flutterwave</c> in IConfiguration.
/// </summary>
public sealed class FlutterwaveOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Flutterwave";

    /// <summary>Flutterwave secret key (FLWSECK-...). Used as the Bearer token on every request.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Flutterwave public key (FLWPUBK-...). Required for tokenised client-side flows.</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>Flutterwave card-encryption key. Used by card-tokenisation flows.</summary>
    public string EncryptionKey { get; set; } = string.Empty;

    /// <summary>
    /// Webhook secret hash. Flutterwave sends this verbatim in the <c>verif-hash</c> header — there
    /// is no HMAC. The provider compares against this value using a constant-time comparison.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Default redirect URL Flutterwave will send the customer to after a hosted-payment.</summary>
    public string? RedirectUrl { get; set; }

    /// <summary>Override the Flutterwave base URL. Leave null in normal use (defaults to https://api.flutterwave.com/).</summary>
    public string? BaseUrl { get; set; }
}
