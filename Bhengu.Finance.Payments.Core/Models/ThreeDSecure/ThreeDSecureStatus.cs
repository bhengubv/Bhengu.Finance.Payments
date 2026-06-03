// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.ThreeDSecure;

/// <summary>
/// Result of a 3-D Secure / SCA authentication attempt.
/// </summary>
public enum ThreeDSecureStatus
{
    /// <summary>Authentication succeeded — charge can proceed with liability shift.</summary>
    Authenticated,

    /// <summary>Authentication is required and the payer must complete a challenge (redirect / iframe).</summary>
    ChallengeRequired,

    /// <summary>Payer attempted authentication but was not fully verified (issuer "attempted" status). Liability shift may still apply on Visa.</summary>
    Attempted,

    /// <summary>Authentication is not required for this transaction (e.g. low-value exemption, allow-listed merchant).</summary>
    NotRequired,

    /// <summary>Authentication failed terminally — charge MUST NOT proceed.</summary>
    Failed
}
