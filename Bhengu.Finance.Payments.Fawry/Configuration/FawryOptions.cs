// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Fawry.Configuration;

/// <summary>
/// Configuration for the Fawry provider. Bound from <c>Bhengu:Finance:Payments:Fawry</c> in IConfiguration.
/// </summary>
public sealed class FawryOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Fawry";

    /// <summary>Fawry-issued merchant code (a.k.a. "merchantCode").</summary>
    public string MerchantCode { get; set; } = string.Empty;

    /// <summary>Fawry-issued security key used for SHA-256 request signing and webhook verification.</summary>
    public string SecurityKey { get; set; } = string.Empty;

    /// <summary>Default payment method when the caller doesn't supply one in metadata. CARD | MWALLET | PAYATFAWRY.</summary>
    public string DefaultPaymentMethod { get; set; } = "CARD";

    /// <summary>URL Fawry redirects the payer to after a hosted-checkout payment completes.</summary>
    public string? ReturnUrl { get; set; }

    /// <summary>URL Fawry POSTs notifications (webhooks) to.</summary>
    public string? NotificationUrl { get; set; }

    /// <summary>Use the sandbox / staging environment instead of live.</summary>
    public bool UseSandbox { get; set; } = true;

    /// <summary>Override the live Fawry base URL. Leave null in normal use.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Override the sandbox Fawry base URL. Leave null in normal use.</summary>
    public string? SandboxUrl { get; set; }
}
