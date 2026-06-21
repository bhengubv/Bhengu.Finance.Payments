// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Onafriq.Configuration;

/// <summary>
/// Configuration for the Onafriq (formerly MFS Africa) provider. Bound from
/// <c>Bhengu:Finance:Payments:Onafriq</c> in IConfiguration.
/// <para>
/// The Onafriq "Portal API" is the Beyonic API (Onafriq acquired Beyonic in 2020 and rebranded the
/// developer portal). Endpoints, the <c>Authorization: Token …</c> scheme, and the request/response
/// shapes are documented at https://developer.mfsafrica.com/docs/api-endpoints and the open-source
/// reference https://github.com/beyonic/api-docs.
/// </para>
/// </summary>
public sealed class OnafriqOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Onafriq";

    /// <summary>
    /// API key sent as <c>Authorization: Token &lt;ApiKey&gt;</c> on every request.
    /// Source: https://developer.mfsafrica.com/docs/api-key (Token Based Authentication).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional account ID to debit/credit, sent as the <c>account</c> form field when set. Onafriq
    /// defaults to the organisation's primary account when omitted, so this is optional.
    /// Source: https://github.com/beyonic/api-docs (sending_funds/_payments, collecting_funds/_collection_requests).
    /// </summary>
    public string? AccountId { get; set; }

    /// <summary>
    /// Base URL for the Portal API. Defaults to the cross-border production host
    /// <c>https://api.mfsafrica.com/api/</c>.
    /// <para>
    /// NOTE: Onafriq has no separate sandbox HOST — testing is done against the same base URL using the
    /// <c>BXC</c> test currency, enabled per-organisation in the portal (Settings → Advanced → Test tools).
    /// Cross-border-enabled accounts are routed to <c>https://mfsafrica.beyonicpartners.com/api/</c>; set
    /// <see cref="BaseUrl"/> to that host if your account is provisioned there.
    /// Source: https://developer.mfsafrica.com/docs/api-endpoints and
    /// https://github.com/beyonic/api-docs (_testing.md).
    /// </para>
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Optional API version pin sent as the <c>Beyonic-Version</c> header (e.g. <c>v1</c>). When null the
    /// portal applies the organisation's saved default version.
    /// Source: https://github.com/beyonic/api-docs (_versioning.md).
    /// </summary>
    public string? ApiVersion { get; set; }

    /// <summary>
    /// URL Onafriq POSTs asynchronous status callbacks to, sent as the <c>callback_url</c> form field.
    /// Source: https://github.com/beyonic/api-docs (sending_funds/_payments — callback_url).
    /// </summary>
    public string? CallbackUrl { get; set; }

    /// <summary>
    /// Optional HTTP Basic Auth username Onafriq presents on inbound webhook callbacks. Onafriq does NOT
    /// HMAC-sign webhooks; it optionally authenticates the callback with HTTP Basic Auth credentials you
    /// arrange with their support team. Set both <see cref="WebhookBasicAuthUsername"/> and
    /// <see cref="WebhookBasicAuthPassword"/> to have <c>VerifyWebhookSignature</c> validate the inbound
    /// <c>Authorization: Basic …</c> header. Source: https://github.com/beyonic/api-docs (methods/_webhooks.md).
    /// </summary>
    public string? WebhookBasicAuthUsername { get; set; }

    /// <summary>Optional HTTP Basic Auth password Onafriq presents on inbound webhook callbacks. See <see cref="WebhookBasicAuthUsername"/>.</summary>
    public string? WebhookBasicAuthPassword { get; set; }
}
