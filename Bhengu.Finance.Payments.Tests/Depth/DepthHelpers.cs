// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Diagnostics;
using System.Net;
using System.Text;
using Bhengu.Finance.Payments.Core.Security;
using Bhengu.Finance.Payments.Tests.TestHelpers;

namespace Bhengu.Finance.Payments.Tests.Depth;

/// <summary>
/// Shared helpers for the <c>Depth</c> test suite — concurrency, cancellation,
/// signature-security, and malformed-payload resilience tests. Extends the
/// production <see cref="StubHttpMessageHandler"/> with a thread-safe call counter
/// (<see cref="CountingStubHttpMessageHandler"/>) and per-URL delay configuration
/// (<see cref="DelayedStubHttpMessageHandler"/>), and provides
/// <see cref="MeasureEqualityTiming"/> for timing-attack assertions against
/// <see cref="SignatureHelpers.ConstantTimeEquals"/>.
/// </summary>
public static class DepthHelpers
{
    /// <summary>
    /// Microbenchmark the <see cref="SignatureHelpers.ConstantTimeEquals(string, string)"/>
    /// function in three failure scenarios. The whole point of a constant-time
    /// comparison is that the wall-clock time of a failed comparison must not depend
    /// on where the first differing byte sits — short-circuiting on the very first
    /// byte should NOT be faster than failing on the last byte.
    /// </summary>
    /// <param name="secret">The expected value (the "match" side of every comparison).</param>
    /// <param name="match">A successful match — establishes the "all bytes scanned" baseline. Must equal <paramref name="secret"/>.</param>
    /// <param name="mismatchAtPosition0">A value of the same length as <paramref name="secret"/> that differs at the very first byte.</param>
    /// <param name="mismatchAtLastPosition">A value of the same length as <paramref name="secret"/> that differs only at the last byte.</param>
    /// <param name="iterations">Iteration count per scenario. 1000 is the documented baseline.</param>
    /// <returns>
    /// Three average elapsed-millisecond values. The two failure averages
    /// (<c>FailFirst</c> and <c>FailLast</c>) should be within ~20% of one another;
    /// the success average (<c>Match</c>) is reported alongside for context.
    /// </returns>
    public static (double Match, double FailFirst, double FailLast) MeasureEqualityTiming(
        string secret,
        string match,
        string mismatchAtPosition0,
        string mismatchAtLastPosition,
        int iterations)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(mismatchAtPosition0);
        ArgumentNullException.ThrowIfNull(mismatchAtLastPosition);
        if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));

        // Warm-up — JIT compilation and CPU caches should be hot before measuring.
        for (var i = 0; i < 200; i++)
        {
            _ = SignatureHelpers.ConstantTimeEquals(secret, match);
            _ = SignatureHelpers.ConstantTimeEquals(secret, mismatchAtPosition0);
            _ = SignatureHelpers.ConstantTimeEquals(secret, mismatchAtLastPosition);
        }

        var matchTotal = TimeOne(() => SignatureHelpers.ConstantTimeEquals(secret, match), iterations);
        var failFirstTotal = TimeOne(() => SignatureHelpers.ConstantTimeEquals(secret, mismatchAtPosition0), iterations);
        var failLastTotal = TimeOne(() => SignatureHelpers.ConstantTimeEquals(secret, mismatchAtLastPosition), iterations);

        return (matchTotal / iterations, failFirstTotal / iterations, failLastTotal / iterations);
    }

    private static double TimeOne(Action body, int iterations)
    {
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) body();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }
}

/// <summary>
/// Thread-safe call-counting variant of <see cref="StubHttpMessageHandler"/>. Wraps a
/// user-supplied handler and atomically increments <see cref="CallCount"/> on every
/// inbound <see cref="HttpRequestMessage"/>, so concurrency tests can assert that an
/// idempotency cache really collapsed N callers onto 1 upstream call.
/// </summary>
public sealed class CountingStubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;
    private int _callCount;

    /// <summary>Construct with the per-request handler that produces the canned response.</summary>
    public CountingStubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) =>
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    /// <summary>Number of <see cref="SendAsync"/> invocations seen so far. Safe to read concurrently.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _callCount);
        return Task.FromResult(_handler(request, ct));
    }
}

/// <summary>
/// Variant of <see cref="StubHttpMessageHandler"/> that honours per-URL response delays AND
/// the request <see cref="CancellationToken"/>. Used by cancellation-propagation tests to
/// fire a long-running OAuth fetch, then cancel mid-flight and assert
/// <see cref="OperationCanceledException"/> propagates to the caller.
/// </summary>
public sealed class DelayedStubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;
    private readonly List<(Func<Uri, bool> Predicate, TimeSpan Delay)> _delays = new();
    private int _callCount;

    /// <summary>Construct with the per-request handler that produces the canned response.</summary>
    public DelayedStubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) =>
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    /// <summary>Number of <see cref="SendAsync"/> invocations seen so far. Safe to read concurrently.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>
    /// Register a delay applied before the response is produced when the request URI matches
    /// <paramref name="predicate"/>. Multiple delays may be registered; the first matching
    /// predicate wins.
    /// </summary>
    public DelayedStubHttpMessageHandler DelayWhen(Func<Uri, bool> predicate, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _delays.Add((predicate, delay));
        return this;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _callCount);
        foreach (var (predicate, delay) in _delays)
        {
            if (predicate(request.RequestUri!))
            {
                // Honour cancellation during the delay — this is the whole point of the helper.
                await Task.Delay(delay, ct).ConfigureAwait(false);
                break;
            }
        }

        // Allow callers to throw on cancellation after the canned delay too.
        ct.ThrowIfCancellationRequested();
        return _handler(request, ct);
    }

    /// <summary>Helper to build a standard JSON OK response.</summary>
    public static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
