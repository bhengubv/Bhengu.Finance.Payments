// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Models.Subscription;

namespace Bhengu.Finance.Payments.Core.Interfaces;

/// <summary>
/// Optional contract for providers that support plans and recurring billing.
/// Implemented by Stripe, Paystack, Razorpay, Flutterwave, PayFast, MercadoPago etc.
/// </summary>
public interface ISubscriptionProvider
{
    /// <summary>The provider this subscription capability belongs to.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Create a recurring-billing plan template. Subscriptions are created against a plan.
    /// </summary>
    /// <exception cref="Exceptions.BhenguPaymentException">Plan could not be created.</exception>
    Task<Plan> CreatePlanAsync(PlanRequest request, CancellationToken ct = default);

    /// <summary>Fetch a plan by reference. Returns null if not found.</summary>
    Task<Plan?> GetPlanAsync(string planReference, CancellationToken ct = default);

    /// <summary>Subscribe a customer to a plan.</summary>
    /// <exception cref="Exceptions.PaymentDeclinedException">First charge (if not trialling) was declined.</exception>
    /// <exception cref="Exceptions.BhenguPaymentException">Subscription could not be created.</exception>
    Task<Subscription> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken ct = default);

    /// <summary>Fetch a subscription by reference. Returns null if not found.</summary>
    Task<Subscription?> GetSubscriptionAsync(string subscriptionReference, CancellationToken ct = default);

    /// <summary>
    /// Cancel a subscription. Idempotent: cancelling an already-cancelled subscription succeeds without error.
    /// </summary>
    /// <param name="immediately">If true, cancel right now; if false, let the current billing cycle complete and cancel at period end (where the provider supports it).</param>
    Task<Subscription> CancelSubscriptionAsync(string subscriptionReference, bool immediately = false, CancellationToken ct = default);

    /// <summary>Pause a subscription — no further charges until resumed. Throws if the provider doesn't support pausing.</summary>
    Task<Subscription> PauseSubscriptionAsync(string subscriptionReference, CancellationToken ct = default);

    /// <summary>Resume a paused subscription.</summary>
    Task<Subscription> ResumeSubscriptionAsync(string subscriptionReference, CancellationToken ct = default);
}
