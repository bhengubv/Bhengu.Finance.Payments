// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Cellulant.Configuration;

/// <summary>
/// Configuration for the Cellulant (Tingg) provider. Bound from
/// <c>Bhengu:Finance:Payments:Cellulant</c> in IConfiguration.
/// </summary>
/// <remarks>
/// Wire details verified against Tingg Checkout 3.0 docs (June 2026):
/// hosts <c>https://api.tingg.africa</c> (live) / <c>https://api-approval.tingg.africa</c> (sandbox);
/// every API call requires the <c>apiKey</c> request header IN ADDITION to the OAuth
/// <c>Authorization: Bearer</c> token. Sources:
/// https://docs.tingg.africa/reference/authenticate-requests and
/// https://docs.tingg.africa/docs/checkout-v3-express-checkout
/// </remarks>
public sealed class CellulantOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Cellulant";

    /// <summary>Tingg service code identifying the merchant service.</summary>
    public string ServiceCode { get; set; } = string.Empty;

    /// <summary>
    /// Tingg API key. Sent as the <c>apiKey</c> request header on EVERY call (OAuth token request,
    /// checkout, refund, query). Distinct from the OAuth <see cref="ClientId"/>/<see cref="ClientSecret"/>.
    /// Source: https://docs.tingg.africa/reference/authenticate-requests
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>OAuth2 client ID used to mint access tokens.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OAuth2 client secret used to mint access tokens.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Merchant transaction identifier prefix used on outbound requests.</summary>
    public string? MerchantTransactionId { get; set; }

    /// <summary>
    /// Customer-facing redirect + webhook URL. Maps to Tingg's <c>success_redirect_url</c>,
    /// <c>fail_redirect_url</c> and <c>callback_url</c> (the IPN/webhook endpoint).
    /// </summary>
    public string? CallbackUrl { get; set; }

    /// <summary>
    /// HMAC-SHA256 webhook secret. UNVERIFIED: Tingg's public Checkout v3 webhook docs do not
    /// document any signature/HMAC header on the callback (see
    /// https://docs.tingg.africa/reference/4-implement-webhook-via-callback-url-1). This is retained
    /// only to preserve prior behaviour for deployments that have an out-of-band signing arrangement;
    /// leave blank if Tingg does not sign your callbacks. See <c>VerifyWebhookSignature</c>.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// ISO-3166 alpha-3 country code (e.g. "KEN", "NGA", "ZAF"). Tingg v3 expects the 3-letter form
    /// (<c>country_code</c>). Defaults to Kenya — Tingg's home market.
    /// Source: https://docs.tingg.africa/docs/checkout-v3-express-checkout
    /// </summary>
    public string CountryCode { get; set; } = "KEN";

    /// <summary>Use the Tingg sandbox/approval endpoint (<c>api-approval.tingg.africa</c>) instead of production.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>
    /// Override the Tingg checkout API base URL. Leave null to use the sandbox/production default
    /// (<c>https://api-approval.tingg.africa/</c> or <c>https://api.tingg.africa/</c>).
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Override the Tingg Payouts (global-api) base URL used for disbursements. Leave null to use
    /// the sandbox/production default. The Payouts API is a SEPARATE Tingg product from Checkout.
    /// Source: https://docs.tingg.africa/reference/postpayment (host
    /// <c>https://api-approval.tingg.africa/v1/global-api/payments</c>).
    /// </summary>
    public string? PayoutBaseUrl { get; set; }
}
