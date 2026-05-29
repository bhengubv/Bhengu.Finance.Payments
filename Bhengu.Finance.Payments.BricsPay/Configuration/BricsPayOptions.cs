// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.BricsPay.Configuration;

/// <summary>
/// Configuration for the BRICS Pay provider. Bound from <c>Bhengu:Finance:Payments:BricsPay</c>.
/// </summary>
public sealed class BricsPayOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:BricsPay";

    /// <summary>BRICS Pay merchant ID.</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>BRICS Pay request-signing secret.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>BRICS Pay webhook-signing secret (separate from SecretKey).</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>If true, routes to sandbox.bricspay.org. False = production.</summary>
    public bool UseSandbox { get; set; } = false;

    /// <summary>Override the production base URL. Leave null in normal use.</summary>
    public string? BaseUrlOverride { get; set; }

    /// <summary>Override the sandbox base URL. Leave null in normal use.</summary>
    public string? SandboxUrlOverride { get; set; }
}
