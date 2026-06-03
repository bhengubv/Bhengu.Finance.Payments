// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using Bhengu.Finance.Payments.Core.Models.Subscription;

namespace Bhengu.Finance.Payments.PayFast.Internals;

/// <summary>
/// Thread-safe in-process cache of PayFast subscription plan templates.
/// PayFast has no first-class Plan resource — plan parameters (amount/currency/interval/cycles)
/// are inlined on each redirect form-post. We persist the params under a UUID so callers can
/// pass a single "plan reference" through their checkout flow.
/// </summary>
/// <remarks>
/// State lives for the lifetime of the cache instance (typically the process). Consumers that
/// require cross-process or cross-deployment plan persistence should externalise the plan store
/// to their own database and bypass <see cref="Providers.PayFastSubscriptionProvider"/>'s plan
/// helpers entirely.
/// </remarks>
public sealed class PayFastPlanCache
{
    private readonly ConcurrentDictionary<string, Plan> _plans = new(StringComparer.Ordinal);

    /// <summary>Register a plan and return the generated reference.</summary>
    /// <param name="request">Plan template to store.</param>
    /// <returns>The cached <see cref="Plan"/> with its assigned <see cref="Plan.Reference"/>.</returns>
    public Plan Add(PlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reference = $"pfplan-{Guid.NewGuid():N}";
        var plan = new Plan
        {
            Reference = reference,
            Name = request.Name,
            Amount = request.Amount,
            Currency = request.Currency,
            Interval = request.Interval,
            TotalCycles = request.TotalCycles,
            Description = request.Description
        };
        _plans[reference] = plan;
        return plan;
    }

    /// <summary>Fetch a previously-registered plan, or <c>null</c> if no such reference.</summary>
    public Plan? Get(string reference)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        return _plans.TryGetValue(reference, out var plan) ? plan : null;
    }

    /// <summary>Remove a plan from the cache.</summary>
    public bool Remove(string reference)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        return _plans.TryRemove(reference, out _);
    }
}
