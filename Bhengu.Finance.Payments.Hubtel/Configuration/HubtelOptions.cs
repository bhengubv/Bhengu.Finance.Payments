// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Hubtel.Configuration;

/// <summary>
/// Configuration for the Hubtel (Ghana) provider. Bound from <c>Bhengu:Finance:Payments:Hubtel</c> in IConfiguration.
/// <para>
/// Hubtel splits its surface across two documented hosts, so this provider targets each per-operation
/// rather than via a single base address:
/// </para>
/// <list type="bullet">
///   <item><description>Online Checkout (hosted redirect) — <c>https://payproxyapi.hubtel.com/items/initiate</c>.
///     Source: https://businessdocs-developers.hubtel.com/docs/api-reference-online-checkout</description></item>
///   <item><description>Merchant Account (Send/Receive Mobile Money, Refund, Statement) —
///     <c>https://api.hubtel.com/v1/merchantaccount/...</c>.
///     Source: https://developers.hubtel.com/ (Merchant Account API); base host corroborated by the
///     official-shape PHP/JS clients (ovac/hubtel-payment Api.php, paulmajora/hubtelpayment).</description></item>
/// </list>
/// </summary>
public sealed class HubtelOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Hubtel";

    /// <summary>Hubtel API client id. Used as the Basic-auth username.
    /// Source (Basic ClientId:ClientSecret): https://businessdocs-developers.hubtel.com/docs/api-reference-online-checkout</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Hubtel API client secret. Used as the Basic-auth password.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Hubtel merchant account number (POS Sales Number). Required on collections/payouts/statements.</summary>
    public string MerchantAccountNumber { get; set; } = string.Empty;

    /// <summary>
    /// Optional shared secret for an HMAC-SHA256(hex) check over the raw callback body.
    /// <para>
    /// UNVERIFIED: Hubtel's Online Checkout callback is NOT cryptographically signed — there is no
    /// documented signature header or HMAC on the callback POST (confirmed: businessdocs
    /// /reference/checkout-callback shows no signature; community write-ups note the gap explicitly,
    /// e.g. medium.com/@verbsgh ".../security-matters..."). Authenticity is meant to be established by
    /// re-checking the transaction via Hubtel's status API and/or matching a single-use ClientReference.
    /// This field therefore only gates an OPTIONAL non-standard guard for deployments that put their own
    /// HMAC proxy in front of the callback; leave it empty to treat callbacks as unsigned.
    /// </para>
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Merchant primary callback URL (the payment-result IPN posted by Hubtel).</summary>
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>Hosted-page redirect-after-completion URL (Online Checkout <c>returnUrl</c>).</summary>
    public string ReturnUrl { get; set; } = string.Empty;

    /// <summary>Hosted-page cancellation URL (Online Checkout <c>cancellationUrl</c>). Falls back to <see cref="ReturnUrl"/> when unset.</summary>
    public string CancellationUrl { get; set; } = string.Empty;

    /// <summary>ISO 4217 default currency. Defaults to "GHS".</summary>
    public string Currency { get; set; } = "GHS";

    /// <summary>When true, the sandbox host overrides are used. Defaults to false.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>
    /// Online Checkout host for the hosted-redirect initiate call. Defaults to
    /// <c>https://payproxyapi.hubtel.com/</c>.
    /// Source: https://businessdocs-developers.hubtel.com/docs/api-reference-online-checkout
    /// </summary>
    public string? CheckoutBaseUrl { get; set; }

    /// <summary>
    /// Merchant-Account host for Send/Receive Mobile Money, Refund and Statement calls. Defaults to
    /// <c>https://api.hubtel.com/</c> (paths carry the <c>v1/merchantaccount/...</c> prefix).
    /// Source: https://developers.hubtel.com/ (Merchant Account API).
    /// </summary>
    public string? MerchantBaseUrl { get; set; }

    /// <summary>Optional sandbox override for <see cref="CheckoutBaseUrl"/>.</summary>
    public string? CheckoutSandboxUrl { get; set; }

    /// <summary>Optional sandbox override for <see cref="MerchantBaseUrl"/>.</summary>
    public string? MerchantSandboxUrl { get; set; }

    // === Resolved hosts (trailing-slash-normalised) ===

    /// <summary>Default Online Checkout host. Source: businessdocs-developers.hubtel.com/docs/api-reference-online-checkout</summary>
    public const string DefaultCheckoutBaseUrl = "https://payproxyapi.hubtel.com/";

    /// <summary>Default Merchant-Account host. Source: developers.hubtel.com (Merchant Account API).</summary>
    public const string DefaultMerchantBaseUrl = "https://api.hubtel.com/";

    /// <summary>The effective Online Checkout base URI (sandbox-aware, trailing slash guaranteed).</summary>
    public Uri ResolvedCheckoutBaseUrl => Normalise(
        UseSandbox ? CheckoutSandboxUrl ?? CheckoutBaseUrl ?? DefaultCheckoutBaseUrl
                   : CheckoutBaseUrl ?? DefaultCheckoutBaseUrl);

    /// <summary>The effective Merchant-Account base URI (sandbox-aware, trailing slash guaranteed).</summary>
    public Uri ResolvedMerchantBaseUrl => Normalise(
        UseSandbox ? MerchantSandboxUrl ?? MerchantBaseUrl ?? DefaultMerchantBaseUrl
                   : MerchantBaseUrl ?? DefaultMerchantBaseUrl);

    private static Uri Normalise(string raw)
    {
        if (!raw.EndsWith('/')) raw += "/";
        return new Uri(raw);
    }
}
