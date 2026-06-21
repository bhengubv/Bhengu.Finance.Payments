// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Moniepoint.Configuration;

/// <summary>
/// Configuration for the Moniepoint provider. Moniepoint's developer API is <b>Monnify</b>
/// (<c>api.monnify.com</c>); this provider integrates it. Bound from <c>Bhengu:Finance:Payments:Moniepoint</c>.
/// </summary>
public sealed class MoniepointOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Moniepoint";

    /// <summary>Monnify API key — the username half of the Basic-auth login.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Monnify secret key — the password half of the Basic-auth login; also keys webhook signatures.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Monnify contract code — required to initialise transactions.</summary>
    public string ContractCode { get; set; } = string.Empty;

    /// <summary>Wallet / source account number used as the source of disbursements (single transfers).</summary>
    public string WalletAccountNumber { get; set; } = string.Empty;

    /// <summary>
    /// Optional explicit webhook-signing secret. When empty, <see cref="SecretKey"/> is used — Monnify
    /// signs the <c>monnify-signature</c> header with the secret key (HMAC-SHA512).
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Redirect URL the payer is sent to after completing checkout.</summary>
    public string RedirectUrl { get; set; } = string.Empty;

    /// <summary>When true, requests target the Monnify sandbox (<c>sandbox.monnify.com</c>) instead of production.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the Monnify base URL. Leave null to use the live/sandbox default.</summary>
    public string? BaseUrl { get; set; }
}
