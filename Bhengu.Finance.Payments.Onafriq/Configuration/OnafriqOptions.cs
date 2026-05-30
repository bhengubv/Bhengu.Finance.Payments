// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Onafriq.Configuration;

/// <summary>
/// Configuration for the Onafriq (formerly MFS Africa) provider. Bound from
/// <c>Bhengu:Finance:Payments:Onafriq</c> in IConfiguration.
/// </summary>
public sealed class OnafriqOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Onafriq";

    /// <summary>API key sent in the <c>X-API-Key</c> header on every request.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Merchant ID issued by Onafriq.</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256 webhook secret used to verify the <c>X-Signature</c> header.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>URL Onafriq POSTs asynchronous status callbacks to.</summary>
    public string? CallbackUrl { get; set; }

    /// <summary>Use the Onafriq sandbox endpoint instead of production.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the Onafriq base URL. Leave null to use the sandbox/production default.</summary>
    public string? BaseUrl { get; set; }
}
