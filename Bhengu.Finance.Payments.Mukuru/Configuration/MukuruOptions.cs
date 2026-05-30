// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Mukuru.Configuration;

/// <summary>
/// Configuration for the Mukuru B2B remittance provider. Bound from
/// <c>Bhengu:Finance:Payments:Mukuru</c> in IConfiguration.
/// Mukuru's primary capability is South Africa outbound remittance to other African corridors;
/// <see cref="Core.Interfaces.IPayoutProvider"/> wraps Create-Transaction and
/// <see cref="Core.Interfaces.IPaymentGatewayProvider.ProcessPaymentAsync"/> wraps Wallet-Topup.
/// </summary>
public sealed class MukuruOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Mukuru";

    /// <summary>OAuth2 client_id for the merchant's Mukuru B2B integration.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OAuth2 client_secret companion to <see cref="ClientId"/>.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Mukuru merchant identifier.</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>Webhook signing secret. Mukuru HMACs the body with SHA-256 in <c>X-Mukuru-Signature</c>.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>ISO-3166 alpha-2 sender country. Mukuru is SA-centric; defaults to "ZA".</summary>
    public string SenderCountry { get; set; } = "ZA";

    /// <summary>Default ISO 4217 currency code. Defaults to "ZAR".</summary>
    public string DefaultCurrency { get; set; } = "ZAR";

    /// <summary>Callback URL for transaction lifecycle webhooks.</summary>
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>If true, requests are routed to <see cref="SandboxUrl"/> instead of <see cref="BaseUrl"/>.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Production base URL (default https://api.mukuru.com).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Sandbox base URL (default https://api-sandbox.mukuru.com).</summary>
    public string? SandboxUrl { get; set; }
}
