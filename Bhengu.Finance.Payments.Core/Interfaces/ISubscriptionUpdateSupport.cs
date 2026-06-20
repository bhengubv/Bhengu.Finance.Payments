// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Models.Subscription;

namespace Bhengu.Finance.Payments.Core.Interfaces;

/// <summary>
/// Optional add-on contract for subscription providers that support updating an active subscription's
/// terms in place — amount, interval, next-billing date, remaining cycles. Implemented by PayFast
/// (<c>PATCH subscriptions/{token}/update</c>). Providers that don't support in-place updates simply
/// don't implement this; consumers query via <c>subscriptionProvider as ISubscriptionUpdateSupport</c>
/// and gate at compile time instead of catching a runtime "not supported" exception.
/// </summary>
public interface ISubscriptionUpdateSupport
{
    /// <summary>
    /// Update an active subscription's terms. Only the non-null fields on <paramref name="request"/>
    /// are sent to the provider; everything else is left unchanged.
    /// </summary>
    Task<Subscription> UpdateSubscriptionAsync(
        string subscriptionReference,
        SubscriptionUpdateRequest request,
        CancellationToken ct = default);
}
