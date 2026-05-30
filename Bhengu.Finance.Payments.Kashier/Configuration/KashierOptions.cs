// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Kashier.Configuration;

/// <summary>
/// Configuration for the Kashier provider. Bound from <c>Bhengu:Finance:Payments:Kashier</c> in IConfiguration.
/// </summary>
public sealed class KashierOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Kashier";

    /// <summary>Kashier API key. Sent as the Authorization header on every API call.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Kashier merchant identifier (MID).</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>Kashier-issued secret key used for hosted-payment-page hash generation.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256 secret used to verify inbound webhook payloads (x-kashier-signature header).</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Default ISO 4217 currency code. Kashier supports EGP, USD, EUR, AED, SAR.</summary>
    public string Currency { get; set; } = "EGP";

    /// <summary>Operating mode — "test" or "live". Drives the hosted-payment-page query string.</summary>
    public string Mode { get; set; } = "test";

    /// <summary>URL Kashier redirects the payer to after a hosted-checkout completion.</summary>
    public string? RedirectUrl { get; set; }

    /// <summary>URL Kashier POSTs webhooks to.</summary>
    public string? ServerWebhookUrl { get; set; }

    /// <summary>Use the sandbox environment. Affects the default Mode value.</summary>
    public bool UseSandbox { get; set; } = true;

    /// <summary>Override the Kashier API base URL. Leave null in normal use (defaults to https://api.kashier.io/).</summary>
    public string? BaseUrl { get; set; }
}
