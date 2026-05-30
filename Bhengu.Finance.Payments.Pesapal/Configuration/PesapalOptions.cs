// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Pesapal.Configuration;

/// <summary>
/// Configuration for the Pesapal provider. Bound from <c>Bhengu:Finance:Payments:Pesapal</c> in IConfiguration.
/// </summary>
public sealed class PesapalOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Pesapal";

    /// <summary>Pesapal Consumer Key. Used to obtain a Bearer token via /api/Auth/RequestToken.</summary>
    public string ConsumerKey { get; set; } = string.Empty;

    /// <summary>Pesapal Consumer Secret. Paired with ConsumerKey at auth time.</summary>
    public string ConsumerSecret { get; set; } = string.Empty;

    /// <summary>The registered IPN id (obtained once via /api/URLSetup/RegisterIPN). Required on SubmitOrderRequest.</summary>
    public string IpnId { get; set; } = string.Empty;

    /// <summary>Merchant return URL the customer is redirected to after the hosted-payment-page flow.</summary>
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>Merchant IPN URL (only used for one-time RegisterIPN; not required on every charge).</summary>
    public string IpnUrl { get; set; } = string.Empty;

    /// <summary>ISO 4217 default currency. Defaults to "KES" (Kenyan Shilling).</summary>
    public string Currency { get; set; } = "KES";

    /// <summary>When true, the SandboxUrl is used. Defaults to false (live).</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the Pesapal live base URL. Defaults to https://pay.pesapal.com/v3.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Override the Pesapal sandbox base URL. Defaults to https://cybqa.pesapal.com/pesapalv3.</summary>
    public string? SandboxUrl { get; set; }
}
