// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Marketplace;

namespace Bhengu.Finance.Payments.Core.Interfaces;

/// <summary>
/// Optional contract for providers that support marketplace / split-payment flows where the gross
/// charge is automatically apportioned across multiple sub-merchant accounts.
/// Implemented by Stripe Connect, Paystack (Splits + Subaccounts), Flutterwave (Subaccounts + Splits),
/// Razorpay (Route), MercadoPago (Marketplace).
/// </summary>
public interface IMarketplaceProvider
{
    /// <summary>The provider this marketplace capability belongs to.</summary>
    string ProviderName { get; }

    /// <summary>Onboard a new sub-merchant account. May return an <see cref="SubAccount.OnboardingUrl"/> for hosted KYC.</summary>
    /// <exception cref="Exceptions.ProviderUnavailableException">Provider was unreachable.</exception>
    Task<SubAccount> CreateSubAccountAsync(SubAccountRequest request, CancellationToken ct = default);

    /// <summary>Fetch a sub-account by reference. Returns null if not found.</summary>
    Task<SubAccount?> GetSubAccountAsync(string subAccountReference, CancellationToken ct = default);

    /// <summary>List all sub-accounts on the platform.</summary>
    Task<IReadOnlyList<SubAccount>> ListSubAccountsAsync(CancellationToken ct = default);

    /// <summary>Create a reusable split definition.</summary>
    Task<SplitDefinition> CreateSplitAsync(SplitDefinitionRequest request, CancellationToken ct = default);

    /// <summary>Fetch a split by reference. Returns null if not found.</summary>
    Task<SplitDefinition?> GetSplitAsync(string splitReference, CancellationToken ct = default);

    /// <summary>
    /// Charge a payment method and atomically split the proceeds.
    /// </summary>
    /// <exception cref="Exceptions.PaymentDeclinedException">The charge was declined.</exception>
    /// <exception cref="Exceptions.BhenguPaymentException">Split rules invalid (overlapping shares, unknown sub-account, etc).</exception>
    Task<PaymentResponse> ChargeWithSplitAsync(ChargeWithSplitRequest request, CancellationToken ct = default);
}
