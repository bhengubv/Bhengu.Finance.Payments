// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Interswitch.Configuration;

/// <summary>
/// Configuration for the Interswitch provider. Bound from <c>Bhengu:Finance:Payments:Interswitch</c>.
/// </summary>
public sealed class InterswitchOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Interswitch";

    /// <summary>Interswitch Passport OAuth2 client id. Used in Basic-auth header for token exchange.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Interswitch Passport OAuth2 client secret. Used in Basic-auth header for token exchange.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Merchant code issued by Interswitch.</summary>
    public string MerchantCode { get; set; } = string.Empty;

    /// <summary>Quickteller Pay product id (also known as PayItem code).</summary>
    public string ProductId { get; set; } = string.Empty;

    /// <summary>Terminal id for POS-style flows. Optional for online cashier flows.</summary>
    public string? TerminalId { get; set; }

    /// <summary>HMAC-SHA512 secret used to verify inbound webhook signatures (<c>X-Interswitch-Signature</c>).</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>When true, requests target the sandbox URL instead of the production one.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the production base URL (defaults to https://passport.interswitchng.com).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Override the sandbox base URL (defaults to https://qa.interswitchng.com).</summary>
    public string? SandboxUrl { get; set; }
}
