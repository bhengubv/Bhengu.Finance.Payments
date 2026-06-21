// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.EcoCash.Configuration;

/// <summary>
/// Configuration for the EcoCash (Zimbabwe) provider, bound from
/// <c>Bhengu:Finance:Payments:EcoCash</c> in IConfiguration.
/// <para>
/// Targets the public <b>EcoCash Open API</b> (developers.ecocash.co.zw). That API authenticates with a
/// single <c>X-API-KEY</c> header and carries the merchant identity inside the API key itself — there is
/// no Basic-auth username/password, merchant PIN, or merchant MSISDN on the wire (those belonged to the
/// older, merchant-gated EcoCash gateway, which this provider does NOT target).
/// </para>
/// </summary>
public sealed class EcoCashOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:EcoCash";

    /// <summary>
    /// EcoCash Open API key, sent in the <c>X-API-KEY</c> header on every request. Issued from the
    /// EcoCash developer portal; the merchant identity is bound to this key server-side.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Use the EcoCash Open API sandbox path family (<c>.../c2b/sandbox</c>) instead of live
    /// (<c>.../c2b/live</c>). The host is identical for both; only the trailing path segment differs.
    /// </summary>
    public bool UseSandbox { get; set; }

    /// <summary>
    /// Override the EcoCash Open API base URL. Leave null to use the documented default
    /// <c>https://developers.ecocash.co.zw/api/ecocash_pay</c>. The sandbox/live suffix is appended
    /// per-endpoint from <see cref="UseSandbox"/>.
    /// </summary>
    public string? BaseUrl { get; set; }
}
