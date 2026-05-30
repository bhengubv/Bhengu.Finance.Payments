// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Remita.Configuration;

/// <summary>
/// Configuration for the Remita (SystemSpecs) provider. Bound from
/// <c>Bhengu:Finance:Payments:Remita</c> in IConfiguration.
/// Remita uses SHA-512 hex hashes of concatenated fields with the API key for authentication.
/// </summary>
public sealed class RemitaOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Remita";

    /// <summary>Merchant ID issued by SystemSpecs (also called "remitaConsumerKey").</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>Service Type ID for the configured collection service.</summary>
    public string ServiceTypeId { get; set; } = string.Empty;

    /// <summary>Remita API key. Used inside SHA-512 hash construction for every request.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Optional API token / secret companion to ApiKey for newer flows.</summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>Funding bank code for Single Send Money payouts (required for IPayoutProvider calls).</summary>
    public string FromBank { get; set; } = string.Empty;

    /// <summary>Debit account number used for payouts.</summary>
    public string DebitAccount { get; set; } = string.Empty;

    /// <summary>Default ISO 4217 currency code. Remita is Nigeria-centric; defaults to "NGN".</summary>
    public string Currency { get; set; } = "NGN";

    /// <summary>Callback URL the Remita hosted-checkout will POST status updates to.</summary>
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>If true, requests are routed to <see cref="SandboxUrl"/> instead of <see cref="BaseUrl"/>.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Production base URL (default https://login.remita.net).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Sandbox base URL (default https://remitademo.net).</summary>
    public string? SandboxUrl { get; set; }
}
