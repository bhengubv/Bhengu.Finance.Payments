// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Stripe.Configuration;

/// <summary>
/// Configuration for the Stripe provider. Bound from <c>Bhengu:Finance:Payments:Stripe</c> in IConfiguration.
/// </summary>
public sealed class StripeOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Stripe";

    /// <summary>Stripe secret API key (sk_live_... or sk_test_...). Used for all server-side calls.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Stripe webhook signing secret (whsec_...). Used to verify webhook payloads.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Optional Stripe Connect <c>client_id</c> (ca_...). Required by Marketplace flows that issue
    /// OAuth links for Standard Connect accounts. Express / Custom flows that use account links
    /// only need <see cref="SecretKey"/>.
    /// </summary>
    public string ConnectClientId { get; set; } = string.Empty;
}
