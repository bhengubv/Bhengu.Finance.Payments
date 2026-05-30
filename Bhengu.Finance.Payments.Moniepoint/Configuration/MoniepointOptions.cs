// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Moniepoint.Configuration;

/// <summary>
/// Configuration for the Moniepoint provider. Bound from <c>Bhengu:Finance:Payments:Moniepoint</c>.
/// </summary>
public sealed class MoniepointOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Moniepoint";

    /// <summary>Moniepoint API key. Used as the Bearer token on every request.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Webhook secret used to verify HMAC-SHA512 signatures on inbound webhooks (<c>x-moniepoint-signature</c>).</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Merchant id supplied by Moniepoint.</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>Redirect URL the payer is sent to after completing checkout.</summary>
    public string RedirectUrl { get; set; } = string.Empty;

    /// <summary>When true, requests target the sandbox URL instead of the production one.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the Moniepoint base URL. Leave null in normal use (defaults to https://api.moniepoint.com/).</summary>
    public string? BaseUrl { get; set; }
}
