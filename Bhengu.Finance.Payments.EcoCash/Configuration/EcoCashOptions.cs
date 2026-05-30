// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.EcoCash.Configuration;

/// <summary>
/// Configuration for the EcoCash (Zimbabwe) provider. Bound from
/// <c>Bhengu:Finance:Payments:EcoCash</c> in IConfiguration.
/// </summary>
public sealed class EcoCashOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:EcoCash";

    /// <summary>EcoCash API key sent in the <c>X-Api-Key</c> header on every request.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>EcoCash Basic-auth username.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>EcoCash Basic-auth password.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Merchant code issued by EcoCash.</summary>
    public string MerchantCode { get; set; } = string.Empty;

    /// <summary>Merchant PIN.</summary>
    public string MerchantPin { get; set; } = string.Empty;

    /// <summary>Merchant mobile number used as the receiving merchant identifier.</summary>
    public string MerchantNumber { get; set; } = string.Empty;

    /// <summary>URL EcoCash will POST asynchronous status callbacks to.</summary>
    public string? NotifyUrl { get; set; }

    /// <summary>Use the EcoCash sandbox endpoint instead of production.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the EcoCash base URL. Leave null to use the sandbox/production default driven by <see cref="UseSandbox"/>.</summary>
    public string? BaseUrl { get; set; }
}
