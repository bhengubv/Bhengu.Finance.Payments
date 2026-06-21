// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Kashier.Configuration;

/// <summary>
/// Configuration for the Kashier provider. Bound from <c>Bhengu:Finance:Payments:Kashier</c> in IConfiguration.
/// </summary>
/// <remarks>
/// Kashier issues two credentials per account (dashboard → Integrations): a <b>Payment API Key</b> and a
/// <b>Secret Key</b>. Their roles per Kashier's documentation:
/// <list type="bullet">
///   <item><b>Secret Key</b> — used as the <c>Authorization</c> header on the server REST API
///         (<c>/checkout</c>, refund, order reconciliation). Source: developers.kashier.io order-reconciliation
///         and refund pages, and the asciisd/kashier SDK (<c>Authorization =&gt; getSecretKey()</c>).</item>
///   <item><b>Payment API Key</b> — used to verify inbound webhook signatures
///         (<c>x-kashier-signature</c>). Source: developers.kashier.io webhook page
///         ("Payment API Keys are used to generate hash orders and to validate signatures").</item>
/// </list>
/// The hosted-payment-page / iframe <b>order hash</b> is keyed by the Secret Key per the official
/// integration guide (www.kashier.io/docs/integration-guide), which states the hash is signed with
/// "yourServiceSecretKey, NOT the API Key", and ships a verifiable test vector. Note: Kashier's own GitHub
/// demo and the asciisd SDK key the same hash with the API Key — many merchant accounts have
/// apiKey == secretKey, which is why both forms are seen in the wild.
/// </remarks>
public sealed class KashierOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Kashier";

    /// <summary>
    /// Kashier <b>Payment API Key</b>. Used to verify inbound webhook signatures (<c>x-kashier-signature</c>).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Kashier merchant identifier (MID).</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>
    /// Kashier <b>Secret Key</b>. Sent as the <c>Authorization</c> header on every server REST call and used
    /// to key the hosted-payment-page order hash.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional override for the webhook-signing key. Leave blank in normal use — Kashier signs webhooks with
    /// the Payment API Key (<see cref="ApiKey"/>), which is the default when this is empty. Retained for
    /// accounts that were issued a distinct webhook secret.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Default ISO 4217 currency code. Kashier supports EGP, USD, EUR, AED, SAR.</summary>
    public string Currency { get; set; } = "EGP";

    /// <summary>Operating mode — "test" or "live". Drives the hosted-payment-page query string.</summary>
    public string Mode { get; set; } = "test";

    /// <summary>URL Kashier redirects the payer to after a hosted-checkout completion.</summary>
    public string? RedirectUrl { get; set; }

    /// <summary>URL Kashier POSTs webhooks to.</summary>
    public string? ServerWebhookUrl { get; set; }

    /// <summary>
    /// Use the sandbox environment. When true the REST base resolves to <c>https://test-api.kashier.io</c>;
    /// when false to <c>https://api.kashier.io</c>. Also drives the default <see cref="Mode"/>.
    /// </summary>
    public bool UseSandbox { get; set; } = true;

    /// <summary>
    /// Override the Kashier REST API base URL. Leave null in normal use — it is derived from
    /// <see cref="UseSandbox"/> (<c>https://test-api.kashier.io</c> / <c>https://api.kashier.io</c>).
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Override the hosted-payment-page / iframe base URL. Leave null in normal use — defaults to
    /// <c>https://checkout.kashier.io</c>.
    /// </summary>
    public string? HostedPaymentBaseUrl { get; set; }
}
