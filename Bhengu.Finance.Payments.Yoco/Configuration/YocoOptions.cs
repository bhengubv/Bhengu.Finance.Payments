// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Yoco.Configuration;

/// <summary>
/// Configuration for the Yoco provider. Bound from <c>Bhengu:Finance:Payments:Yoco</c> in IConfiguration.
/// </summary>
public sealed class YocoOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Yoco";

    /// <summary>Yoco secret API key. Used as the Bearer token on every request.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Yoco webhook signing secret used to verify inbound webhook payloads.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Override the Yoco base URL. Leave null in normal use (defaults to https://online.yoco.com/v1/).</summary>
    public string? BaseUrl { get; set; }
}
