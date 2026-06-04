// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Models.Subscription;

namespace Bhengu.Finance.Payments.PayFast.Internals;

/// <summary>
/// Distributed-cache-backed registry of PayFast subscription plan templates.
/// PayFast has no first-class Plan resource — plan parameters (amount/currency/interval/cycles)
/// are inlined on each redirect form-post. We persist the params under a UUID so callers can
/// pass a single "plan reference" through their checkout flow.
/// </summary>
/// <remarks>
/// Entries are written to <see cref="IBhenguDistributedCache"/> with a 365-day TTL so plan
/// templates survive process restarts and remain consistent across replicas when Redis is wired
/// up via the optional <c>Bhengu.Finance.Payments.Redis</c> package.
/// </remarks>
public sealed class PayFastPlanCache
{
    private const string KeyPrefix = "payfast:plan:";
    private static readonly TimeSpan TimeToLive = TimeSpan.FromDays(365);

    private readonly IBhenguDistributedCache _cache;

    /// <summary>Construct with an injected distributed cache. Used in DI-driven scenarios.</summary>
    public PayFastPlanCache(IBhenguDistributedCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>Default-constructor convenience for tests and back-compat callers.</summary>
    public PayFastPlanCache() : this(new InMemoryBhenguDistributedCache()) { }

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
        // Fire-and-forget cache write; the synchronous return preserves the public API.
        _cache.SetAsync(KeyPrefix + reference, plan, TimeToLive).GetAwaiter().GetResult();
        return plan;
    }

    /// <summary>Fetch a previously-registered plan, or <c>null</c> if no such reference.</summary>
    public Plan? Get(string reference)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        return _cache.GetAsync<Plan>(KeyPrefix + reference).GetAwaiter().GetResult();
    }

    /// <summary>Remove a plan from the cache. Returns true if the plan was previously cached.</summary>
    public bool Remove(string reference)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        var existed = _cache.GetAsync<Plan>(KeyPrefix + reference).GetAwaiter().GetResult() is not null;
        _cache.RemoveAsync(KeyPrefix + reference).GetAwaiter().GetResult();
        return existed;
    }
}
