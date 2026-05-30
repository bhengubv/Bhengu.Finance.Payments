// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.DPO.Configuration;

/// <summary>
/// Configuration for the DPO Group (Network International) provider. Bound from
/// <c>Bhengu:Finance:Payments:DPO</c> in IConfiguration.
/// </summary>
public sealed class DPOOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:DPO";

    /// <summary>DPO CompanyToken — sent in the body of every authenticated API request.</summary>
    public string CompanyToken { get; set; } = string.Empty;

    /// <summary>Default ServiceType (e.g. "3854"). Used on every <c>createToken</c> request.</summary>
    public string ServiceType { get; set; } = string.Empty;

    /// <summary>Default service description.</summary>
    public string? ServiceDescription { get; set; }

    /// <summary>URL DPO redirects the customer to after a successful payment.</summary>
    public string? RedirectUrl { get; set; }

    /// <summary>URL DPO sends the customer back to if they abandon the payment.</summary>
    public string? BackUrl { get; set; }

    /// <summary>Use the DPO sandbox endpoint instead of production.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the DPO base URL. Leave null in normal use (defaults to https://secure.3gdirectpay.com/).</summary>
    public string? BaseUrl { get; set; }
}
