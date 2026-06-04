// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Diagnostics.Metrics;
using Bhengu.Finance.Payments.Core.Observability;

namespace Bhengu.Finance.Payments.Tests.TestHelpers;

/// <summary>
/// Subscribes to <see cref="BhenguPaymentDiagnostics"/> counters and histograms via a
/// <see cref="MeterListener"/>. Used by per-provider diagnostics tests to assert that
/// <c>ProcessPaymentAsync</c> et al. increment the right counter with the right tags.
/// </summary>
public sealed class DiagnosticsCounterRecorder : IDisposable
{
    private readonly MeterListener _listener;
    private readonly List<(string Instrument, long Value, IReadOnlyDictionary<string, object?> Tags)> _measurementsLong = new();
    private readonly List<(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags)> _measurementsDouble = new();
    private readonly object _lock = new();

    /// <summary>Construct + start listening. Dispose to stop.</summary>
    public DiagnosticsCounterRecorder()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == BhenguPaymentDiagnostics.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((inst, measurement, tagSpan, _) =>
        {
            var tags = new Dictionary<string, object?>();
            foreach (var t in tagSpan) tags[t.Key] = t.Value;
            lock (_lock) _measurementsLong.Add((inst.Name, measurement, tags));
        });
        _listener.SetMeasurementEventCallback<double>((inst, measurement, tagSpan, _) =>
        {
            var tags = new Dictionary<string, object?>();
            foreach (var t in tagSpan) tags[t.Key] = t.Value;
            lock (_lock) _measurementsDouble.Add((inst.Name, measurement, tags));
        });
        _listener.Start();
    }

    /// <summary>Total counter increments for <paramref name="instrument"/> matching the supplied provider.</summary>
    public long CounterTotalFor(string instrument, string provider)
    {
        lock (_lock)
        {
            return _measurementsLong
                .Where(m => m.Instrument == instrument && m.Tags.TryGetValue("provider", out var p) && p?.ToString() == provider)
                .Sum(m => m.Value);
        }
    }

    /// <summary>Number of histogram observations for <paramref name="instrument"/> matching the supplied provider.</summary>
    public int HistogramObservationsFor(string instrument, string provider)
    {
        lock (_lock)
        {
            return _measurementsDouble
                .Count(m => m.Instrument == instrument && m.Tags.TryGetValue("provider", out var p) && p?.ToString() == provider);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _listener.Dispose();
}
