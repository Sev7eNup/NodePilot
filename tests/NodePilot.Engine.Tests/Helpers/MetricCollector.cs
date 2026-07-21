using System.Diagnostics.Metrics;

namespace NodePilot.Engine.Tests.Helpers;

/// <summary>
/// Lightweight in-memory meter listener used only by tests. Captures every measurement
/// recorded against a given meter name. Disposable — always wrap in <c>using</c>.
/// </summary>
public sealed class MetricCollector : IDisposable
{
    private readonly MeterListener _listener;
    private readonly List<Measurement> _measurements = new();
    private readonly object _sync = new();

    public IReadOnlyList<Measurement> Measurements
    {
        get
        {
            lock (_sync) return _measurements.ToArray();
        }
    }

    public MetricCollector(params string[] meterNames)
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (Array.IndexOf(meterNames, instrument.Meter.Name) >= 0)
                    listener.EnableMeasurementEvents(instrument);
            },
        };
        _listener.SetMeasurementEventCallback<long>(Record);
        _listener.SetMeasurementEventCallback<double>(RecordDouble);
        _listener.SetMeasurementEventCallback<int>((i, v, t, s) => Record(i, v, t, s));
        _listener.Start();
    }

    public long SumLong(string instrumentName, params (string Key, object? Value)[] requiredTags)
    {
        return (long)Measurements
            .Where(m => m.InstrumentName == instrumentName && HasAllTags(m, requiredTags))
            .Sum(m => m.Value);
    }

    public int Count(string instrumentName, params (string Key, object? Value)[] requiredTags)
    {
        return Measurements.Count(m => m.InstrumentName == instrumentName && HasAllTags(m, requiredTags));
    }

    public double? MaxDouble(string instrumentName, params (string Key, object? Value)[] requiredTags)
    {
        var samples = Measurements
            .Where(m => m.InstrumentName == instrumentName && HasAllTags(m, requiredTags))
            .Select(m => m.Value)
            .ToArray();
        return samples.Length == 0 ? null : samples.Max();
    }

    private static bool HasAllTags(Measurement m, (string Key, object? Value)[] requiredTags)
    {
        foreach (var (key, value) in requiredTags)
        {
            if (!m.Tags.TryGetValue(key, out var actual)) return false;
            if (!Equals(actual?.ToString(), value?.ToString())) return false;
        }
        return true;
    }

    private void Record(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        Capture(instrument, (double)value, tags);
    }

    private void RecordDouble(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        Capture(instrument, value, tags);
    }

    private void Capture(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var tagDict = new Dictionary<string, object?>();
        foreach (var kv in tags) tagDict[kv.Key] = kv.Value;
        lock (_sync)
        {
            _measurements.Add(new Measurement(instrument.Meter.Name, instrument.Name, value, tagDict));
        }
    }

    public void Dispose() => _listener.Dispose();

    public sealed record Measurement(string MeterName, string InstrumentName, double Value, IReadOnlyDictionary<string, object?> Tags);
}
