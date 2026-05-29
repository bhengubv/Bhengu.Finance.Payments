// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.PayFast.Configuration;

/// <summary>
/// Configuration for the PayFast provider. Bound from <c>Bhengu:Finance:Payments:PayFast</c> in IConfiguration.
/// </summary>
public sealed class PayFastOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:PayFast";

    /// <summary>PayFast merchant ID issued by PayFast on signup.</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>PayFast merchant key issued by PayFast on signup.</summary>
    public string MerchantKey { get; set; } = string.Empty;

    /// <summary>The shared passphrase used to sign requests. Required if "Require signature" is on in the merchant dashboard.</summary>
    public string Passphrase { get; set; } = string.Empty;

    /// <summary>If true, all requests go to PayFast sandbox (sandbox.payfast.co.za) instead of production.</summary>
    public bool UseSandbox { get; set; } = false;

    /// <summary>Default return URL after a successful redirect payment. May be overridden per request.</summary>
    public string? ReturnUrl { get; set; }

    /// <summary>Default cancel URL after a cancelled redirect payment. May be overridden per request.</summary>
    public string? CancelUrl { get; set; }

    /// <summary>URL PayFast posts the ITN webhook to. Required for redirect-flow payments to be settled.</summary>
    public string? NotifyUrl { get; set; }

    /// <summary>Override of the production PayFast base URL — leave null to use the default https://www.payfast.co.za.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Override of the sandbox PayFast base URL — leave null to use the default https://sandbox.payfast.co.za.</summary>
    public string? SandboxUrl { get; set; }
}
