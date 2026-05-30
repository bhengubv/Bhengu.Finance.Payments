// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.ExpressPay.Configuration;

/// <summary>
/// Configuration for the ExpressPay (Ghana / West Africa) provider.
/// Bound from <c>Bhengu:Finance:Payments:ExpressPay</c> in IConfiguration.
/// </summary>
public sealed class ExpressPayOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:ExpressPay";

    /// <summary>ExpressPay merchant id (sent as <c>merchant-id</c> in submit.php).</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>ExpressPay api key (sent as <c>api-key</c> in submit.php).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>URL the customer is redirected to after the hosted-page completes.</summary>
    public string RedirectUrl { get; set; } = string.Empty;

    /// <summary>Server-to-server post URL ExpressPay calls with payment result.</summary>
    public string PostUrl { get; set; } = string.Empty;

    /// <summary>ISO 4217 default currency. Defaults to "GHS".</summary>
    public string Currency { get; set; } = "GHS";

    /// <summary>When true, the SandboxUrl is used. Defaults to false.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the ExpressPay live base URL. Defaults to https://expresspay.com.gh/api.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Override the ExpressPay sandbox base URL. Defaults to https://sandbox.expresspaygh.com/api.</summary>
    public string? SandboxUrl { get; set; }
}
