// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Models.Vault;

namespace Bhengu.Finance.Payments.Core.Interfaces;

/// <summary>
/// Optional contract for providers that can vault payment-method details for re-use.
/// Implemented by Stripe, Paystack (Authorization codes), Razorpay (Tokens), Flutterwave, Yoco etc.
/// Providers that do not vault (PayShap, raw mobile-money rails) simply do not implement this interface;
/// consumers should query via <c>provider as ITokenisationProvider</c> and gate behaviour on the result.
/// </summary>
public interface ITokenisationProvider
{
    /// <summary>The provider this tokenisation capability belongs to.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Tokenise raw card details and attach the resulting token to a vault customer.
    /// </summary>
    /// <remarks>
    /// Most merchants should prefer client-side hosted-field tokenisation (Stripe Elements, Razorpay Checkout)
    /// over passing raw PAN through their server. This method exists for SAQ-D merchants and for
    /// non-card payment methods where raw details legitimately transit the server.
    /// </remarks>
    /// <exception cref="Exceptions.PaymentDeclinedException">The provider rejected the card (e.g. CVV/AVS mismatch on a tokenisation pre-auth).</exception>
    /// <exception cref="Exceptions.ProviderUnavailableException">The provider was unreachable.</exception>
    Task<PaymentMethod> TokeniseAsync(TokeniseRequest request, CancellationToken ct = default);

    /// <summary>
    /// Fetch the descriptor for an existing vaulted method.
    /// </summary>
    /// <returns>The payment-method descriptor, or <c>null</c> if the token is not recognised.</returns>
    /// <exception cref="Exceptions.ProviderUnavailableException">The provider was unreachable.</exception>
    Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// List the vaulted payment methods for a customer.
    /// </summary>
    /// <exception cref="Exceptions.ProviderUnavailableException">The provider was unreachable.</exception>
    Task<IReadOnlyList<PaymentMethod>> ListPaymentMethodsAsync(string customerId, CancellationToken ct = default);

    /// <summary>
    /// Remove a vaulted payment method. Subsequent charges using its token will be declined.
    /// </summary>
    /// <returns>True if the method existed and was deleted; false if no such token.</returns>
    /// <exception cref="Exceptions.ProviderUnavailableException">The provider was unreachable.</exception>
    Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default);
}
