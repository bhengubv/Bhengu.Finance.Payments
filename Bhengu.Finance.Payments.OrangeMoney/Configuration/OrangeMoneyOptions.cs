// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.OrangeMoney.Configuration;

/// <summary>
/// Configuration for the Orange Money Web Payment provider.
/// Bound from <c>Bhengu:Finance:Payments:OrangeMoney</c> in IConfiguration.
/// </summary>
public sealed class OrangeMoneyOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:OrangeMoney";

    /// <summary>OAuth2 Consumer Key issued in the Orange Developer Portal.</summary>
    public string ConsumerKey { get; set; } = string.Empty;

    /// <summary>OAuth2 Consumer Secret. Used with <see cref="ConsumerKey"/> for Basic auth on token exchange.</summary>
    public string ConsumerSecret { get; set; } = string.Empty;

    /// <summary>The merchant key issued by Orange Money. Embedded in every Web Payment request body.</summary>
    public string MerchantKey { get; set; } = string.Empty;

    /// <summary>Two-letter country code segment in the path (e.g. <c>ci</c>, <c>sn</c>, <c>cm</c>, <c>ml</c>).</summary>
    public string Country { get; set; } = "ci";

    /// <summary>URL the payer is redirected back to on success.</summary>
    public string ReturnUrl { get; set; } = string.Empty;

    /// <summary>URL the payer is redirected back to on cancellation.</summary>
    public string CancelUrl { get; set; } = string.Empty;

    /// <summary>Server-side notification URL Orange Money POSTs the result to.</summary>
    public string NotifUrl { get; set; } = string.Empty;

    /// <summary>Use the sandbox base URL when true.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the production base URL. Defaults to https://api.orange.com/.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Override the sandbox base URL. Defaults to https://api.orange.com/ (sandbox path-scoped).</summary>
    public string? SandboxUrl { get; set; }
}
