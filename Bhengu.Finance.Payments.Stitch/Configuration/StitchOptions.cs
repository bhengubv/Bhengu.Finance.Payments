// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Stitch.Configuration;

/// <summary>
/// Configuration for the Stitch open-banking provider. Bound from
/// <c>Bhengu:Finance:Payments:Stitch</c> in IConfiguration. The SDK uses the simpler
/// API-key flow against the Stitch GraphQL endpoint for ProcessPaymentAsync and
/// ProcessPayoutAsync; full OAuth2 client-assertion is also supported via
/// <see cref="ClientAssertionJwt"/>.
/// </summary>
public sealed class StitchOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Stitch";

    /// <summary>Stitch client identifier (issued by Stitch dashboard).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>API key passed in the <c>X-API-Key</c> header for the simpler auth flow.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Webhook signing secret (HMAC-SHA256 in <c>X-Stitch-Signature</c>).</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Optional pre-built JWT used for the full OAuth2 client_credentials + client-assertion
    /// flow (<c>POST /connect/token</c>). When set, the SDK exchanges it for a bearer token instead
    /// of using <see cref="ApiKey"/>.
    /// </summary>
    public string? ClientAssertionJwt { get; set; }

    /// <summary>Default beneficiary bank account number used for incoming pay-by-bank requests.</summary>
    public string BeneficiaryAccountNumber { get; set; } = string.Empty;

    /// <summary>Default beneficiary bank ID (e.g. "absa", "fnb", "standardbank", "nedbank", "capitec").</summary>
    public string BeneficiaryBankId { get; set; } = string.Empty;

    /// <summary>Beneficiary account holder name.</summary>
    public string BeneficiaryName { get; set; } = string.Empty;

    /// <summary>Default ISO 4217 currency code. Defaults to "ZAR".</summary>
    public string Currency { get; set; } = "ZAR";

    /// <summary>If true, requests are routed to <see cref="SandboxUrl"/> instead of <see cref="BaseUrl"/>.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Production REST base URL (default https://api.stitch.money).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>GraphQL endpoint URL (default https://api.stitch.money/graphql).</summary>
    public string? GraphqlEndpoint { get; set; }

    /// <summary>Sandbox base URL (default https://api-staging.stitch.money).</summary>
    public string? SandboxUrl { get; set; }
}
