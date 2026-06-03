// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.ThreeDSecure;

/// <summary>
/// The provider's response to <see cref="Interfaces.IThreeDSecureProvider.StartAuthenticationAsync"/>.
/// When <see cref="Status"/> is <see cref="ThreeDSecureStatus.ChallengeRequired"/> the consumer must
/// redirect the payer to <see cref="RedirectUrl"/>, then resume the charge by passing
/// <see cref="ThreeDSecureCompletion"/> back on <c>PaymentRequest.ThreeDSecureCompletion</c>.
/// </summary>
public sealed record ThreeDSecureChallenge
{
    /// <summary>Outcome of the initial authentication check.</summary>
    public required ThreeDSecureStatus Status { get; init; }

    /// <summary>Provider-issued challenge identifier — pass back on <see cref="ThreeDSecureCompletion.ChallengeReference"/> to resume.</summary>
    public required string ChallengeReference { get; init; }

    /// <summary>
    /// URL the consumer must send the payer to in order to complete the authentication challenge.
    /// Only set when <see cref="Status"/> is <see cref="ThreeDSecureStatus.ChallengeRequired"/>.
    /// </summary>
    public string? RedirectUrl { get; init; }

    /// <summary>
    /// Provider-specific payload required to render an iframe-based challenge (e.g. Stripe's
    /// <c>client_secret</c>, Adyen's challenge JWT). Use when embedding rather than redirecting.
    /// </summary>
    public string? ChallengePayload { get; init; }

    /// <summary>3DS protocol version the issuer is using (e.g. "2.2.0").</summary>
    public string? ProtocolVersion { get; init; }

    /// <summary>Issuer-supplied transaction identifier (DS Trans ID).</summary>
    public string? DsTransactionId { get; init; }
}
