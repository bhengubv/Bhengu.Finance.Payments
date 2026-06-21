// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.BricsPay.Configuration;

/// <summary>
/// The asymmetric algorithm used to sign BRICS Pay requests. The public half is registered with the
/// processor at terminal onboarding; the private half stays here. (BRICS Pay also documents GOST 34.10,
/// which .NET does not support natively — it is therefore out of scope for this provider.)
/// </summary>
public enum BricsPaySignatureAlgorithm
{
    /// <summary>ECDSA over curve prime256v1 (P-256) with SHA-256. The BRICS Pay default.</summary>
    Ecdsa = 0,

    /// <summary>RSA (2048-bit recommended) with SHA-256 and PKCS#1 v1.5 padding.</summary>
    Rsa = 1
}

/// <summary>
/// Configuration for the BRICS Pay QR ("Internet Acquiring") provider. Bound from
/// <c>Bhengu:Finance:Payments:BricsPay</c>.
/// <para>
/// BRICS Pay e-commerce acceptance is QR-code acquiring on the Joys processing platform. There is no
/// self-serve sandbox: a terminal (<see cref="TerminalId"/>) is provisioned at onboarding, the
/// <see cref="BaseUrl"/> is issued to you then, and you register the public half of
/// <see cref="PrivateKeyPem"/> with the processor. See <c>BRICS_PAY_API_REFERENCE.md</c>.
/// </para>
/// </summary>
public sealed class BricsPayOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:BricsPay";

    /// <summary>The payment-terminal ID ("Pos") issued at onboarding.</summary>
    public string TerminalId { get; set; } = string.Empty;

    /// <summary>
    /// The provisioned API base URL for this terminal (the production or test-server root). Required —
    /// there is no public default host; BRICS Pay assigns it per terminal.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// PEM-encoded PRIVATE key used to sign requests. The matching public key must be registered with the
    /// processor at onboarding. Keep this secret.
    /// </summary>
    public string PrivateKeyPem { get; set; } = string.Empty;

    /// <summary>Signature algorithm matching the public key you registered. Defaults to ECDSA P-256.</summary>
    public BricsPaySignatureAlgorithm SignatureAlgorithm { get; set; } = BricsPaySignatureAlgorithm.Ecdsa;

    /// <summary>Optional default callback URL (terminal-level). Overridable by the terminal config.</summary>
    public string? CallbackUrl { get; set; }

    /// <summary>Optional return-to-store URL shown after payment.</summary>
    public string? ReturnUrl { get; set; }

    /// <summary>Optional CSS URL used to style the hosted payment page.</summary>
    public string? CssUrl { get; set; }

    /// <summary>Optional payment-form lifetime in minutes (BRICS Pay defaults to 5 when omitted). 1–255.</summary>
    public byte? DefaultTtlMinutes { get; set; }
}
