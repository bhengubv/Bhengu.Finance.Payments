// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.ApplePay.Configuration;

/// <summary>
/// Configuration for Apple Pay. Bound from <c>Bhengu:Finance:Payments:ApplePay</c>.
/// NOTE: This package currently ships as a scaffold — a real Apple Pay integration requires
/// the merchant to complete the Apple Developer Apple Pay setup, an ApplePay payment processing
/// certificate, and an Apple Pay merchant identifier. See README for completion steps.
/// </summary>
public sealed class ApplePayOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:ApplePay";

    /// <summary>The Apple Pay merchant identifier (merchant.com.your-app).</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>Path to the Apple Pay merchant identity certificate (.pem or .pfx).</summary>
    public string? MerchantCertificatePath { get; set; }

    /// <summary>Password protecting the merchant certificate, if any.</summary>
    public string? MerchantCertificatePassword { get; set; }

    /// <summary>Domain validated with Apple for Apple Pay on the Web. Required for web flows.</summary>
    public string? DomainName { get; set; }
}
