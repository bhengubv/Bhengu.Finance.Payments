// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Hubtel.Configuration;

/// <summary>
/// Configuration for the Hubtel (Ghana) provider. Bound from <c>Bhengu:Finance:Payments:Hubtel</c> in IConfiguration.
/// </summary>
public sealed class HubtelOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Hubtel";

    /// <summary>Hubtel API client id. Used as the Basic-auth username.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Hubtel API client secret. Used as the Basic-auth password.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Hubtel merchant account number (POS id). Required on collections/payouts.</summary>
    public string MerchantAccountNumber { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256 secret used to verify the Signature webhook header.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Merchant primary callback URL (the payment-result IPN).</summary>
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>Hosted-page redirect-after-completion URL.</summary>
    public string ReturnUrl { get; set; } = string.Empty;

    /// <summary>ISO 4217 default currency. Defaults to "GHS".</summary>
    public string Currency { get; set; } = "GHS";

    /// <summary>When true, the SandboxUrl override is used. Defaults to false.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the Hubtel base URL. Defaults to https://api-txnstatus.hubtel.com/.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Optional sandbox base URL override.</summary>
    public string? SandboxUrl { get; set; }
}
