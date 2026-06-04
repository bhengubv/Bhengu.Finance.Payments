// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Models.Dispute;

namespace Bhengu.Finance.Payments.Core.Interfaces;

/// <summary>
/// Optional contract for providers that expose chargeback / dispute lifecycle programmatically.
/// Implemented by Stripe, Paystack, Razorpay etc. Most aggregator / mobile-money providers do NOT
/// expose disputes via API (resolved manually with the carrier) and won't implement this.
/// </summary>
public interface IDisputeProvider
{
    /// <summary>The provider this dispute capability belongs to.</summary>
    string ProviderName { get; }

    /// <summary>Fetch a dispute by reference. Returns null if not found.</summary>
    /// <exception cref="Exceptions.ProviderUnavailableException">Provider was unreachable.</exception>
    Task<Dispute?> GetDisputeAsync(string disputeReference, CancellationToken ct = default);

    /// <summary>
    /// List disputes within an optional UTC date window. Streamed asynchronously so a merchant with
    /// a long dispute history doesn't materialise every dispute in one allocation; the provider
    /// fetches pages from the upstream as the consumer enumerates.
    /// </summary>
    /// <exception cref="Exceptions.ProviderUnavailableException">Provider was unreachable.</exception>
    IAsyncEnumerable<Dispute> ListDisputesAsync(DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default);

    /// <summary>
    /// Submit contesting evidence for an open dispute. After submission the dispute moves to
    /// <see cref="DisputeStatus.UnderReview"/>; the final outcome is delivered via webhook
    /// (<c>DisputeWonEvent</c> / <c>DisputeLostEvent</c>).
    /// </summary>
    /// <exception cref="Exceptions.BhenguPaymentException">Evidence rejected (e.g. dispute window already closed).</exception>
    Task<Dispute> SubmitEvidenceAsync(string disputeReference, DisputeEvidence evidence, CancellationToken ct = default);

    /// <summary>
    /// Accept the dispute without contesting. Funds are immediately reversed to the payer and
    /// the dispute moves to <see cref="DisputeStatus.Accepted"/>.
    /// </summary>
    Task<Dispute> AcceptDisputeAsync(string disputeReference, CancellationToken ct = default);
}
