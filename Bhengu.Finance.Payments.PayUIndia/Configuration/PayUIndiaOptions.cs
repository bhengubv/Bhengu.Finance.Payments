// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.PayUIndia.Configuration;

/// <summary>
/// Configuration for the PayU India provider. Bound from <c>Bhengu:Finance:Payments:PayUIndia</c>
/// in IConfiguration.
/// </summary>
public sealed class PayUIndiaOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:PayUIndia";

    /// <summary>PayU India merchant key. Identifies the merchant on every call.</summary>
    public string MerchantKey { get; set; } = string.Empty;

    /// <summary>PayU India merchant salt. Used in the SHA-512 hash of every signed call and webhook.</summary>
    public string Salt { get; set; } = string.Empty;

    /// <summary>Merchant redirect URL on successful payment.</summary>
    public string SuccessUrl { get; set; } = string.Empty;

    /// <summary>Merchant redirect URL on failed payment.</summary>
    public string FailureUrl { get; set; } = string.Empty;

    /// <summary>ISO 4217 default currency. Defaults to "INR".</summary>
    public string Currency { get; set; } = "INR";

    /// <summary>When true, the SandboxUrl override is used. Defaults to false (production secure.payu.in).</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the PayU India payment base URL. Defaults to https://secure.payu.in/.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Sandbox base URL override. Defaults to https://test.payu.in/.</summary>
    public string? SandboxUrl { get; set; }

    /// <summary>PayU India info-service base URL (verify/refund/payout). Defaults to https://info.payu.in/.</summary>
    public string? InfoBaseUrl { get; set; }
}
