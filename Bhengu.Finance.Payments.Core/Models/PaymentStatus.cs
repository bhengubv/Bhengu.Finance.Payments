// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models;

/// <summary>
/// Canonical payment lifecycle status, normalised across all providers.
/// </summary>
public enum PaymentStatus
{
    /// <summary>Initial state — provider has accepted the request but not yet settled.</summary>
    Pending,
    /// <summary>Provider has settled the payment and funds are committed.</summary>
    Completed,
    /// <summary>Provider declined or the payment otherwise failed terminally.</summary>
    Failed,
    /// <summary>Payer or merchant cancelled before settlement.</summary>
    Cancelled,
    /// <summary>Funds were returned to the payer.</summary>
    Refunded
}
