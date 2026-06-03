// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.ThreeDSecure;

/// <summary>
/// Authentication-completion proof returned by the payer's issuer after a 3DS challenge.
/// Pass this back on <c>PaymentRequest.ThreeDSecureCompletion</c> to resume the original charge
/// with liability shift applied.
/// </summary>
public sealed record ThreeDSecureCompletion
{
    /// <summary>The challenge reference originally returned in <see cref="ThreeDSecureChallenge.ChallengeReference"/>.</summary>
    public required string ChallengeReference { get; init; }

    /// <summary>Final authentication outcome the issuer returned after the challenge.</summary>
    public required ThreeDSecureStatus Status { get; init; }

    /// <summary>
    /// CAVV / Authentication Value the issuer signed — the cryptographic proof a downstream acquirer needs
    /// to apply liability shift. Opaque to consumers; pass through verbatim.
    /// </summary>
    public string? AuthenticationValue { get; init; }

    /// <summary>ECI (Electronic Commerce Indicator) the issuer returned.</summary>
    public string? Eci { get; init; }

    /// <summary>Issuer-supplied transaction identifier (DS Trans ID).</summary>
    public string? DsTransactionId { get; init; }
}
