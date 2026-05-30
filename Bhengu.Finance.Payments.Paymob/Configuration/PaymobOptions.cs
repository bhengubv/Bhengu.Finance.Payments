// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Paymob.Configuration;

/// <summary>
/// Configuration for the Paymob provider. Bound from <c>Bhengu:Finance:Payments:Paymob</c> in IConfiguration.
/// </summary>
public sealed class PaymobOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Paymob";

    /// <summary>Paymob API key (used to obtain an auth_token via /api/auth/tokens).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>HMAC secret used to verify inbound transaction-processed callbacks.</summary>
    public string HmacSecret { get; set; } = string.Empty;

    /// <summary>Default Paymob integration_id for ProcessPayment when the caller doesn't supply one in metadata.</summary>
    public int IntegrationId { get; set; }

    /// <summary>Default Paymob iframe_id used to build the iframe URL returned in the PaymentResponse message.</summary>
    public int IframeId { get; set; }

    /// <summary>Default ISO 4217 currency code (Paymob is currency-aware per-integration; default to EGP).</summary>
    public string Currency { get; set; } = "EGP";

    /// <summary>Use the sandbox environment. Paymob shares a single base URL but accept.paymob.com is environment-aware via the merchant account.</summary>
    public bool UseSandbox { get; set; } = true;

    /// <summary>Override the Paymob base URL. Leave null in normal use (defaults to https://accept.paymob.com).</summary>
    public string? BaseUrl { get; set; }
}
