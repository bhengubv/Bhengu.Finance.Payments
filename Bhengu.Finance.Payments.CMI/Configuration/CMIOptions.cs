// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.CMI.Configuration;

/// <summary>
/// Configuration for the CMI (Centre Monetique Interbancaire) provider.
/// Bound from <c>Bhengu:Finance:Payments:CMI</c> in IConfiguration.
/// </summary>
public sealed class CMIOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:CMI";

    /// <summary>CMI-issued merchant clientid.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>CMI-issued storekey used to compute the SHA-512 redirect hash and validate callbacks.</summary>
    public string StoreKey { get; set; } = string.Empty;

    /// <summary>API user name used in the XML CC5Request envelope for inquiry/refund.</summary>
    public string ApiUser { get; set; } = string.Empty;

    /// <summary>API password used in the XML CC5Request envelope.</summary>
    public string ApiPassword { get; set; } = string.Empty;

    /// <summary>URL CMI redirects the payer to on a successful 3D Secure outcome.</summary>
    public string? OkUrl { get; set; }

    /// <summary>URL CMI redirects the payer to on a failed 3D Secure outcome.</summary>
    public string? FailUrl { get; set; }

    /// <summary>URL CMI posts asynchronous notifications to.</summary>
    public string? CallbackUrl { get; set; }

    /// <summary>ISO 4217 numeric currency code. Default 504 (MAD).</summary>
    public string Currency { get; set; } = "504";

    /// <summary>UI language for the hosted 3D Secure page. Default "en".</summary>
    public string Lang { get; set; } = "en";

    /// <summary>Use the CMI test environment (testpayment.cmi.co.ma) instead of live.</summary>
    public bool UseSandbox { get; set; } = true;

    /// <summary>Override the live base URL. Leave null in normal use (defaults to https://payment.cmi.co.ma).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Override the sandbox base URL. Leave null in normal use (defaults to https://testpayment.cmi.co.ma).</summary>
    public string? SandboxUrl { get; set; }
}
