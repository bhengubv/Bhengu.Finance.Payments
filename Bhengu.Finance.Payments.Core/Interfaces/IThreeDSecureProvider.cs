// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.ThreeDSecure;

namespace Bhengu.Finance.Payments.Core.Interfaces;

/// <summary>
/// Optional contract for providers that expose an explicit 3-D Secure / SCA step-up flow.
///
/// Implementations let consumers pre-check whether a charge needs authentication (e.g. for cart-display
/// UX) and resume the original charge after the payer has completed an issuer-hosted challenge.
///
/// Providers that handle 3DS entirely opaquely inside <c>ProcessPaymentAsync</c> (most modern card
/// gateways do this) need NOT implement this — they signal authentication-required by throwing
/// <see cref="Exceptions.BhenguPaymentException"/> with a redirect URL on <c>PaymentResponse.RedirectUrl</c>.
/// </summary>
public interface IThreeDSecureProvider
{
    /// <summary>The provider this 3DS capability belongs to.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Begin a 3DS authentication for the supplied charge intent. Returns a challenge descriptor
    /// the consumer can act on (redirect / embed / proceed-as-frictionless).
    /// </summary>
    /// <param name="chargeIntent">The charge that would be submitted if authentication passes.</param>
    /// <exception cref="Exceptions.ProviderUnavailableException">The provider was unreachable.</exception>
    Task<ThreeDSecureChallenge> StartAuthenticationAsync(PaymentRequest chargeIntent, CancellationToken ct = default);

    /// <summary>
    /// Fetch the current status of a pending challenge — useful for polling-driven UIs.
    /// </summary>
    Task<ThreeDSecureChallenge> GetChallengeAsync(string challengeReference, CancellationToken ct = default);
}
