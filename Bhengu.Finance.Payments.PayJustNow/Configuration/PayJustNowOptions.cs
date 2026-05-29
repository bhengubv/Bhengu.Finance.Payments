// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.PayJustNow.Configuration;

/// <summary>
/// Configuration for the PayJustNow BNPL provider. Bound from <c>Bhengu:Finance:Payments:PayJustNow</c>.
/// </summary>
public sealed class PayJustNowOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:PayJustNow";

    /// <summary>PayJustNow API key sent on the X-Api-Key header.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>PayJustNow secret key used to sign webhooks.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>PayJustNow merchant ID sent on the X-Merchant-Id header.</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>If true, all requests go to sandbox.payjustnow.com instead of production.</summary>
    public bool UseSandbox { get; set; } = false;

    /// <summary>Override the production base URL. Leave null in normal use.</summary>
    public string? BaseUrlOverride { get; set; }

    /// <summary>Override the sandbox base URL. Leave null in normal use.</summary>
    public string? SandboxUrlOverride { get; set; }
}
