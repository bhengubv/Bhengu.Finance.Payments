// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Runtime.CompilerServices;
using Bhengu.Finance.Payments.Core.Models.Vault;

namespace Bhengu.Finance.Payments.Core.Interfaces;

/// <summary>
/// READ-side vault contract. Implemented by every provider that can list / fetch / delete saved
/// payment methods — Stripe, Paystack, Razorpay, Flutterwave, Yoco, MercadoPago, PagSeguro etc.
///
/// <para>This interface deliberately does NOT include the WRITE method that accepts raw PAN. That
/// surface is split into <see cref="IRawCardTokenisationProvider"/> so the PCI-DSS scope impact
/// is impossible to miss at the type-system level.</para>
/// </summary>
public interface ITokenisationProvider
{
    /// <summary>The provider this tokenisation capability belongs to.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Fetch the descriptor for an existing vaulted method.
    /// </summary>
    /// <returns>The payment-method descriptor, or <c>null</c> if the token is not recognised.</returns>
    /// <exception cref="Exceptions.ProviderUnavailableException">The provider was unreachable.</exception>
    Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// List the vaulted payment methods for a customer. Streamed asynchronously so providers with
    /// long vault histories don't materialise everything into memory.
    /// </summary>
    /// <exception cref="Exceptions.ProviderUnavailableException">The provider was unreachable.</exception>
    IAsyncEnumerable<PaymentMethod> ListPaymentMethodsAsync(string customerId, CancellationToken ct = default);

    /// <summary>
    /// Remove a vaulted payment method. Subsequent charges using its token will be declined.
    /// </summary>
    /// <returns>True if the method existed and was deleted; false if no such token.</returns>
    /// <exception cref="Exceptions.ProviderUnavailableException">The provider was unreachable.</exception>
    Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default);
}

/// <summary>
/// PCI-scope-clarifying WRITE counterpart to <see cref="ITokenisationProvider"/>. Implementing
/// this brings the consumer's server into PCI-DSS SAQ-D scope because raw PAN+CVV transits the
/// server. Splitting it from the read surface makes the security implication explicit at
/// compile time — consumers can't accidentally drift into SAQ-D by calling a method on a vault
/// provider they thought was read-only.
///
/// <para><b>Strongly prefer client-side tokenisation</b> (Stripe Elements, Razorpay Standard
/// Checkout, Yoco Inline, Paystack Inline, MercadoPago SDK.js) — the payer's browser sends PAN
/// directly to the provider and your server only ever sees a short-lived token. SAQ stays at A.</para>
/// </summary>
[System.Diagnostics.CodeAnalysis.Experimental("BHENGU_PCI_SAQ_D",
    UrlFormat = "https://github.com/bhengubv/Bhengu.Finance.Payments/blob/master/docs/SECURITY.md#raw-card-tokenisation")]
public interface IRawCardTokenisationProvider
{
    /// <summary>The provider this raw-card tokenisation capability belongs to.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Tokenise raw card details and attach the resulting token to a vault customer.
    /// </summary>
    /// <exception cref="Exceptions.PaymentDeclinedException">The provider rejected the card.</exception>
    /// <exception cref="Exceptions.ProviderUnavailableException">The provider was unreachable.</exception>
    Task<PaymentMethod> TokeniseAsync(TokeniseRequest request, CancellationToken ct = default);
}
