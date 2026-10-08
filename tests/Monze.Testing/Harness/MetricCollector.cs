using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace Monze.Testing.Harness;

/// <summary>
/// Listens to the named meters (for example "Monze", "Monze.Cache",
/// "Npgsql", "System.Runtime") and aggregates every measurement per
/// instrument and tag set: counters and up-down counters are summed,
/// histograms keep count/sum/min/max, gauges and observable instruments keep
/// their last value. <see cref="Snapshot"/> polls the observable instruments
/// first. Each measurement allocates its series key, so this collector is for
/// load and chaos runs, never for a 0 B/op measurement.
/// </summary>
public sealed class MetricCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly HashSet<string> _meters;
    private readonly ConcurrentDictionary<MetricKey, Series> _series = new();

    public MetricCollector(params string[] meterNames)
    {
        _meters = new HashSet<string>(meterNames, StringComparer.Ordinal);
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (_meters.Contains(instrument.Meter.Name))
            {
                listener.EnableMeasurementEvents(instrument, KindOf(instrument));
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) => Record(instrument, value, tags, state));
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, state) => Record(instrument, value, tags, state));
        _listener.SetMeasurementEventCallback<short>((instrument, value, tags, state) => Record(instrument, value, tags, state));
        _listener.SetMeasurementEventCallback<byte>((instrument, value, tags, state) => Record(instrument, value, tags, state));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) => Record(instrument, value, tags, state));
        _listener.SetMeasurementEventCallback<float>((instrument, value, tags, state) => Record(instrument, value, tags, state));
        _listener.Start();
    }

    /// <summary>Polls the observable instruments, then copies every series.</summary>
    public MetricSnapshot Snapshot()
    {
        _listener.RecordObservableInstruments();
        var values = new Dictionary<MetricKey, MetricValue>();
        foreach (var (key, series) in _series)
        {
            values[key] = series.Read();
        }

        return new MetricSnapshot(values);
    }

    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        var kind = (MetricKind)state!;
        var key = new MetricKey(instrument.Meter.Name, instrument.Name, FormatTags(tags));
        _series.GetOrAdd(key, static (_, kind) => new Series(kind), kind).Add(value);
    }

    private static MetricKind KindOf(Instrument instrument)
    {
        if (instrument.IsObservable)
        {
            return MetricKind.Observed;
        }

        var definition = instrument.GetType().IsGenericType ? instrument.GetType().GetGenericTypeDefinition() : null;
        if (definition == typeof(Counter<>))
        {
            return MetricKind.Counter;
        }

        if (definition == typeof(UpDownCounter<>))
        {
            return MetricKind.UpDownCounter;
        }

        if (definition == typeof(Histogram<>))
        {
            return MetricKind.Histogram;
        }

        return MetricKind.Gauge;
    }

    private static string FormatTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0)
        {
            return string.Empty;
        }

        var pairs = new string[tags.Length];
        for (var i = 0; i < tags.Length; i++)
        {
            pairs[i] = tags[i].Key + "=" + Convert.ToString(tags[i].Value, CultureInfo.InvariantCulture);
        }

        Array.Sort(pairs, StringComparer.Ordinal);
        return string.Join(',', pairs);
    }

    private sealed class Series(MetricKind kind)
    {
        private readonly Lock _gate = new();
        private long _count;
        private double _sum;
        private double _min = double.PositiveInfinity;
        private double _max = double.NegativeInfinity;

        public void Add(double value)
        {
            lock (_gate)
            {
                _count++;
                _sum = kind is MetricKind.Gauge or MetricKind.Observed ? value : _sum + value;
                _min = Math.Min(_min, value);
                _max = Math.Max(_max, value);
            }
        }

        public MetricValue Read()
        {
            lock (_gate)
            {
                return new MetricValue(kind, _count, _sum, _count == 0 ? 0 : _min, _count == 0 ? 0 : _max);
            }
        }
    }
}
