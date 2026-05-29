// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Google.Configuration;

/// <summary>
/// Configuration for Google Pay. Bound from <c>Bhengu:Finance:Payments:GooglePay</c>.
/// NOTE: This package currently ships as a scaffold — a real Google Pay integration requires
/// a Google Pay merchant ID and a configured payment processor (Stripe, Adyen, Braintree, etc.)
/// to actually settle transactions. Google Pay itself only tokenises; settlement is downstream.
/// See README for completion steps.
/// </summary>
public sealed class GooglePayOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:GooglePay";

    /// <summary>The Google Pay merchant identifier registered with Google.</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>The downstream processor (stripe|adyen|braintree|cybersource) that actually settles the payment.</summary>
    public string ProcessorName { get; set; } = string.Empty;

    /// <summary>If true, use the Google Pay TEST environment. False = PRODUCTION.</summary>
    public bool UseTestEnvironment { get; set; } = false;
}
