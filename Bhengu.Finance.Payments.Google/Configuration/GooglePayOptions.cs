// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Google.Configuration;

/// <summary>
/// Configuration for Google Pay. Bound from <c>Bhengu:Finance:Payments:GooglePay</c>.
/// Google Pay tokenises only — settlement is performed by the downstream processor named in
/// <see cref="DownstreamProcessor"/> (typically Stripe, Yoco, or Paystack).
/// </summary>
public sealed class GooglePayOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:GooglePay";

    /// <summary>The Google Pay merchant identifier registered with Google.</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>
    /// The downstream processor that actually settles the payment. Must match the
    /// <c>ProviderName</c> of a registered <see cref="Core.Interfaces.IPaymentGatewayProvider"/>
    /// (e.g. "stripe", "yoco", "paystack").
    /// </summary>
    public string DownstreamProcessor { get; set; } = "stripe";

    /// <summary>If true, use the Google Pay TEST environment. False = PRODUCTION.</summary>
    public bool UseTestEnvironment { get; set; } = false;
}
