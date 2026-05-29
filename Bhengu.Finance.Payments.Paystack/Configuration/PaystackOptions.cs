// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Paystack.Configuration;

/// <summary>
/// Configuration for the Paystack provider. Bound from <c>Bhengu:Finance:Payments:Paystack</c>.
/// </summary>
public sealed class PaystackOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Paystack";

    /// <summary>Paystack secret key (sk_live_... or sk_test_...). Used as the Bearer token on every request.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Webhook secret used to verify HMAC-SHA512 signatures on inbound webhooks. Paystack permits
    /// callers to reuse the SecretKey here, but a dedicated secret is recommended.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Optional default e-mail used on charge_authorization calls when the request metadata
    /// has no "email" key. Paystack requires an e-mail on every charge.
    /// </summary>
    public string? DefaultEmail { get; set; }

    /// <summary>Override the Paystack base URL. Leave null in normal use (defaults to https://api.paystack.co/).</summary>
    public string? BaseUrlOverride { get; set; }
}
