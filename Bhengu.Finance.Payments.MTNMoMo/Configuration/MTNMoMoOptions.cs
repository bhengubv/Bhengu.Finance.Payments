// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.MTNMoMo.Configuration;

/// <summary>
/// Configuration for the MTN Mobile Money (MoMo) provider.
/// Bound from <c>Bhengu:Finance:Payments:MTNMoMo</c> in IConfiguration.
/// </summary>
public sealed class MTNMoMoOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:MTNMoMo";

    /// <summary>MoMo subscription key (Primary or Secondary) from the MoMo Developer portal. Sent as <c>Ocp-Apim-Subscription-Key</c>.</summary>
    public string SubscriptionKey { get; set; } = string.Empty;

    /// <summary>The MoMo API User UUID (created via the Provisioning API).</summary>
    public string ApiUserId { get; set; } = string.Empty;

    /// <summary>The MoMo API Key associated with <see cref="ApiUserId"/>. Used with the user ID as Basic auth for token exchange.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The <c>X-Target-Environment</c> header value. Use <c>sandbox</c> for testing, or a market code in production
    /// (e.g. <c>mtnuganda</c>, <c>mtnghana</c>, <c>mtnivorycoast</c>, <c>mtncameroon</c>, <c>mtnzambia</c>).
    /// </summary>
    public string TargetEnvironment { get; set; } = "sandbox";

    /// <summary>Public HTTPS URL MoMo POSTs the transaction-status callback to (passed as <c>X-Callback-Url</c>).</summary>
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>Use the sandbox base URL when true.</summary>
    public bool UseSandbox { get; set; } = true;

    /// <summary>Override the base URL. Defaults to https://sandbox.momodeveloper.mtn.com/ (sandbox) or https://proxy.momoapi.mtn.com/ (production).</summary>
    public string? BaseUrl { get; set; }
}
