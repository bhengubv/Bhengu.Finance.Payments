// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Mandate;

namespace Bhengu.Finance.Payments.Core.Interfaces;

/// <summary>
/// Optional contract for providers that support debit-order / pull-payment mandates.
/// Implemented by Stitch (DebiCheck), Stripe (SEPA/BACS mandates), Razorpay (eMandates),
/// PayFast (recurring tokens) etc.
/// </summary>
public interface IMandateProvider
{
    /// <summary>The provider this mandate capability belongs to.</summary>
    string ProviderName { get; }

    /// <summary>Create a new mandate. The result may carry an <see cref="Mandate.AuthorisationUrl"/> the payer must visit.</summary>
    /// <exception cref="Exceptions.ProviderUnavailableException">Provider was unreachable.</exception>
    Task<Mandate> CreateMandateAsync(MandateRequest request, CancellationToken ct = default);

    /// <summary>Fetch a mandate by reference. Returns null if not found.</summary>
    Task<Mandate?> GetMandateAsync(string mandateReference, CancellationToken ct = default);

    /// <summary>Cancel a mandate. Idempotent: cancelling an already-cancelled mandate succeeds without error.</summary>
    Task<Mandate> CancelMandateAsync(string mandateReference, CancellationToken ct = default);

    /// <summary>
    /// Pull funds against an active mandate. The returned <see cref="PaymentResponse"/> carries the
    /// debit's gateway reference for reconciliation.
    /// </summary>
    /// <exception cref="Exceptions.PaymentDeclinedException">Bank declined the debit (insufficient funds, account closed).</exception>
    /// <exception cref="Exceptions.BhenguPaymentException">Mandate not active, or amount exceeds limit.</exception>
    Task<PaymentResponse> ChargeMandateAsync(MandateChargeRequest request, CancellationToken ct = default);
}
