// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Ozow.Configuration;

/// <summary>
/// Configuration for the Ozow provider. Bound from <c>Bhengu:Finance:Payments:Ozow</c>.
/// </summary>
/// <remarks>
/// The customer-facing charge is a <b>redirect</b> flow: the SDK builds a signed request to
/// <c>https://pay.ozow.com/</c> and the payer is sent there to pay (see <see cref="Providers.OzowPaymentProvider"/>).
/// Server-side operations (transaction status) hit <c>https://api.ozow.com/</c> with the API key on a header.
/// Sources:
/// https://ozow.com/integrations ("you'll need to post the following variables to https://pay.ozow.com"),
/// https://hub.ozow.com/docs (post-variables table + HashCheck rule).
/// </remarks>
public sealed class OzowOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Ozow";

    /// <summary>Ozow site code issued on merchant signup.</summary>
    public string SiteCode { get; set; } = string.Empty;

    /// <summary>Ozow private key used to generate SHA-512 request and webhook hashes.</summary>
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>Ozow API key sent on the <c>ApiKey</c> header for server-side api.ozow.com calls.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// ISO-3166 alpha-2 country code posted as <c>CountryCode</c> (e.g. "ZA"). Part of the HashCheck.
    /// Defaults to "ZA". Source: https://hub.ozow.com/docs post-variables table (CountryCode, String(2)).
    /// </summary>
    public string CountryCode { get; set; } = "ZA";

    /// <summary>
    /// If true, the request is flagged as a test/sandbox transaction. Maps to Ozow's <c>IsTest</c>
    /// post variable, which is part of the HashCheck and is serialised as the lowercase string
    /// "true"/"false". Source: https://hub.ozow.com/docs post-variables table (IsTest, boolean, default false).
    /// </summary>
    public bool UseSandbox { get; set; } = false;

    /// <summary>
    /// Override the customer-redirect base URL (the <c>pay.ozow.com</c> host the payer is sent to).
    /// Leave null to use the default <c>https://pay.ozow.com/</c>.
    /// </summary>
    public string? PaymentBaseUrl { get; set; }

    /// <summary>
    /// Override the server-side API base URL (the <c>api.ozow.com</c> host used for transaction status).
    /// Leave null to use the default <c>https://api.ozow.com/</c>.
    /// </summary>
    public string? ApiBaseUrl { get; set; }
}
