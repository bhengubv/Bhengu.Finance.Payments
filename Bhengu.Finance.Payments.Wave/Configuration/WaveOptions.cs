// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Wave.Configuration;

/// <summary>
/// Configuration for the Wave (West Africa) provider. Bound from
/// <c>Bhengu:Finance:Payments:Wave</c> in IConfiguration.
/// </summary>
public sealed class WaveOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Wave";

    /// <summary>Wave Business API key. Used as the Bearer token on every request.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256 webhook signing secret used to verify the Wave-Signature header.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Default currency for Checkout Sessions when the request does not specify one. Typically XOF or XAF.</summary>
    public string Currency { get; set; } = "XOF";

    /// <summary>Redirect URL used by Wave when a payment succeeds.</summary>
    public string? SuccessUrl { get; set; }

    /// <summary>Redirect URL used by Wave when a payment errors.</summary>
    public string? ErrorUrl { get; set; }

    /// <summary>Override the Wave base URL. Leave null in normal use (defaults to https://api.wave.com/).</summary>
    public string? BaseUrl { get; set; }
}
