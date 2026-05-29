// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Ozow.Configuration;

/// <summary>
/// Configuration for the Ozow provider. Bound from <c>Bhengu:Finance:Payments:Ozow</c>.
/// </summary>
public sealed class OzowOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Ozow";

    /// <summary>Ozow site code issued on merchant signup.</summary>
    public string SiteCode { get; set; } = string.Empty;

    /// <summary>Ozow private key used to generate SHA-512 request and webhook hashes.</summary>
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>Ozow API key sent on the ApiKey header.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>If true, all requests go to api-sandbox.ozow.com instead of production.</summary>
    public bool UseSandbox { get; set; } = false;

    /// <summary>Override the production base URL. Leave null to use the default.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Override the sandbox base URL. Leave null to use the default.</summary>
    public string? SandboxUrl { get; set; }
}
