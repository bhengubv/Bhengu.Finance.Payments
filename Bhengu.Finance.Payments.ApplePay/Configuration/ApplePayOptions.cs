// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.ApplePay.Configuration;

/// <summary>
/// Configuration for Apple Pay. Bound from <c>Bhengu:Finance:Payments:ApplePay</c>.
/// Apple Pay tokenises only — settlement is performed by the downstream processor named in
/// <see cref="DownstreamProcessor"/> (typically Stripe, Yoco, or Paystack).
/// </summary>
public sealed class ApplePayOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:ApplePay";

    /// <summary>The Apple Pay merchant identifier (e.g. merchant.com.your-app).</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>
    /// The downstream processor that actually settles the payment. Must match the
    /// <c>ProviderName</c> of a registered <see cref="Core.Interfaces.IPaymentGatewayProvider"/>
    /// (e.g. "stripe", "yoco", "paystack").
    /// </summary>
    public string DownstreamProcessor { get; set; } = "stripe";

    /// <summary>Domain validated with Apple for Apple Pay on the Web. Required for web flows.</summary>
    public string? DomainName { get; set; }

    /// <summary>Optional path to the merchant identity certificate (for local-decryption flows). Not used when the downstream processor decrypts.</summary>
    public string? MerchantCertificatePath { get; set; }

    /// <summary>Optional password protecting the merchant certificate.</summary>
    public string? MerchantCertificatePassword { get; set; }
}
