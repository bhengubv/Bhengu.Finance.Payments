// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;

namespace Bhengu.Finance.Payments.Flutterwave.Internals;

/// <summary>
/// Client-side idempotency cache for the Flutterwave provider family.
/// <para>
/// Flutterwave's v3 REST API does <b>not</b> expose a native <c>Idempotency-Key</c> header (unlike
/// Stripe). To honour caller-supplied <c>IdempotencyKey</c> values and offer at-least-once-safe
/// retry semantics, we coalesce concurrent calls bearing the same key onto a single in-flight
/// <see cref="Task{TResult}"/> and serve subsequent identical retries from the cached result.
/// </para>
/// <para>
/// <b>In-memory caveat.</b> This dedupe lives in the process. A pair of replicas sitting behind a
/// load balancer can each fire the underlying Flutterwave call once — i.e. up to N times in an
/// N-replica deployment. Distributed dedupe is the caller's responsibility (typically via an
/// upstream message-bus or per-order DB uniqueness constraint). Cache entries are also lost on
/// process recycle.
/// </para>
/// <para>
/// Faulted tasks are evicted so a transient failure does not poison subsequent retries.
/// </para>
/// </summary>
/// <remarks>
/// Internal type — exposed for cross-provider re-use within the Flutterwave assembly only.
/// </remarks>
public sealed class FlutterwaveIdempotencyCache
{
    private readonly ConcurrentDictionary<string, object> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Execute <paramref name="factory"/> exactly once per <paramref name="idempotencyKey"/>, returning
    /// the cached result on subsequent calls. When <paramref name="idempotencyKey"/> is null or empty
    /// the factory is invoked directly with no caching.
    /// </summary>
    /// <typeparam name="T">Result type of the underlying operation.</typeparam>
    /// <param name="idempotencyKey">Caller-supplied dedupe token. Null or whitespace bypasses the cache.</param>
    /// <param name="factory">Async factory producing the result. Only invoked on cache miss.</param>
    /// <returns>The original (or cached) result.</returns>
    public Task<T> GetOrAddAsync<T>(string? idempotencyKey, Func<Task<T>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return factory();

        var task = (Task<T>)_entries.GetOrAdd(idempotencyKey, _ => factory());

        // If the cached task faults, evict so the next retry gets a fresh attempt.
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted || t.IsCanceled)
                _entries.TryRemove(new KeyValuePair<string, object>(idempotencyKey, t));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        return task;
    }

    /// <summary>Remove every cache entry. Intended for tests; production code should not call this.</summary>
    public void Clear() => _entries.Clear();
}
