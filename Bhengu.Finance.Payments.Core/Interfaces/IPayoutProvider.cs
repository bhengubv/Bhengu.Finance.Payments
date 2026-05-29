// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Models;

namespace Bhengu.Finance.Payments.Core.Interfaces;

/// <summary>
/// Optional contract for providers that support paying funds out to a destination.
/// Implemented only by providers that offer payouts (Stripe Connect, Yoco, BRICSPay).
/// PayFast does NOT implement this — PayFast has no payout API.
/// </summary>
public interface IPayoutProvider
{
    /// <summary>The provider this payout capability belongs to.</summary>
    string ProviderName { get; }

    /// <summary>Pay funds out to a tokenised destination.</summary>
    /// <exception cref="Exceptions.ProviderUnavailableException">Provider was unreachable.</exception>
    /// <exception cref="Exceptions.BhenguPaymentException">Payout could not be processed.</exception>
    Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default);
}
