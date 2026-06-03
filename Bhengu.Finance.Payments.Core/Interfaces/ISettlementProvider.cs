// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Models.Settlement;

namespace Bhengu.Finance.Payments.Core.Interfaces;

/// <summary>
/// Optional contract for providers that expose a settlement / reconciliation feed.
/// Implemented by Stripe (Balance Transactions + Payouts), Paystack (Settlements),
/// Razorpay (Settlements), Flutterwave (Settlements) etc.
/// </summary>
public interface ISettlementProvider
{
    /// <summary>The provider this settlement capability belongs to.</summary>
    string ProviderName { get; }

    /// <summary>List settlement batches within a UTC date window.</summary>
    /// <exception cref="Exceptions.ProviderUnavailableException">Provider was unreachable.</exception>
    Task<IReadOnlyList<Settlement>> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Fetch a settlement batch by reference. Returns null if not found.</summary>
    Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default);

    /// <summary>List the constituent transactions inside a settlement batch.</summary>
    Task<IReadOnlyList<SettlementTransaction>> ListTransactionsAsync(string settlementReference, CancellationToken ct = default);
}
