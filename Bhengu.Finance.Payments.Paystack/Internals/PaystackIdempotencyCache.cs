// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;

namespace Bhengu.Finance.Payments.Paystack.Internals;

/// <summary>
/// Client-side idempotency-key deduplication helper for the Paystack provider.
/// Paystack does NOT expose a native idempotency-key header — this cache implements the contract
/// the Bhengu Core surface promises by coalescing concurrent and repeat calls that share the same
/// caller-supplied <c>IdempotencyKey</c> against the same in-flight or completed <see cref="Task{TResult}"/>.
/// </summary>
/// <remarks>
/// <para><strong>In-memory only.</strong> The cache lives in process memory; it does NOT survive process
/// restarts, and it is NOT shared across replicas. A node-crash mid-call followed by a client retry
/// will still reach Paystack a second time. For at-most-once semantics across a restart you must back
/// the cache with a distributed store at the application layer.</para>
/// <para>Entries are retained for the lifetime of the process. The cache is intentionally
/// unbounded — keys are caller-supplied UUIDs that exhibit natural turnover, but pathological
/// callers can grow it. Wrap with eviction at the call site if that is a concern.</para>
/// </remarks>
public sealed class PaystackIdempotencyCache
{
    private readonly ConcurrentDictionary<string, object> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Execute <paramref name="factory"/> at most once for the given <paramref name="idempotencyKey"/>.
    /// Subsequent calls (concurrent or sequential) with the same key return the same <see cref="Task{TResult}"/>
    /// instance, so callers observe an identical result (including a thrown exception).
    /// </summary>
    /// <typeparam name="T">Result type of the factory.</typeparam>
    /// <param name="idempotencyKey">
    /// The caller-supplied idempotency key. When null or whitespace the factory is invoked without
    /// any deduplication — preserves prior behaviour for callers that do not opt in.
    /// </param>
    /// <param name="factory">Delegate that performs the side-effecting work.</param>
    /// <returns>The Task returned by the factory, possibly cached from a prior call.</returns>
    public Task<T> GetOrAddAsync<T>(string? idempotencyKey, Func<Task<T>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return factory();

        var task = (Task<T>)_inFlight.GetOrAdd(idempotencyKey, _ => factory());
        return task;
    }

    /// <summary>
    /// Remove a cached entry. Use after a deliberate retry decision to allow a future call with the
    /// same key to reach the provider again. Most callers will not need this.
    /// </summary>
    /// <returns>True when an entry was evicted; false when the key was not cached.</returns>
    public bool Invalidate(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        return _inFlight.TryRemove(idempotencyKey, out _);
    }
}
