// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.JamboPay.Configuration;

/// <summary>
/// Configuration for the JamboPay provider. Bound from <c>Bhengu:Finance:Payments:JamboPay</c> in IConfiguration.
/// </summary>
public sealed class JamboPayOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:JamboPay";

    /// <summary>JamboPay static API key. Sent in the x-api-key header.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>OAuth2 client id used at /oauth/token (grant_type=client_credentials).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OAuth2 client secret.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>JamboPay merchant code issued at on-boarding.</summary>
    public string MerchantCode { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256 secret used to verify the x-jambopay-signature webhook header.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Merchant callback URL for hosted-page flows.</summary>
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>ISO 4217 default currency. Defaults to "KES".</summary>
    public string Currency { get; set; } = "KES";

    /// <summary>When true, sandbox semantics apply. Defaults to false.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the JamboPay base URL. Defaults to https://api.jambopay.com/v1/.</summary>
    public string? BaseUrl { get; set; }
}
