using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HtpcMatterBridge.Diagnostics;

/// <summary>
/// In-process <see cref="MeterListener"/> over <see cref="AppMetrics"/> that
/// appends JSON-lines snapshots (cumulative counter totals plus histogram
/// count/min/max/avg) to <c>metrics-yyyyMMdd.jsonl</c> next to the app log
/// (ADR-006 §2): one line every 60 s plus a final flush on dispose. Idle
/// churn (S6-1): a flush whose counters/histograms are identical to the last
/// line written to the same file is skipped — an idle day costs one line
/// (the day's first, so the file always exists), not 1440. Prunes snapshot
/// files older than 7 days on construction — the same retention approach as
/// <see cref="Log"/>. The directory is injectable so demos/tests never touch
/// the real user profile. Writing never throws — metrics must not take down
/// the tray app.
/// </summary>
public sealed class MetricsFileListener : IDisposable
{
    private const int RetentionDays = 7;

    private readonly Lock _gate = new();
    private readonly string _directory;
    private readonly MeterListener _listener;
    private readonly System.Threading.Timer _timer;
    private readonly Dictionary<string, long> _counters = [];
    private readonly Dictionary<string, HistogramState> _histograms = [];
    private string? _lastWrittenPayload;
    private string? _lastWrittenPath;
    private bool _disposed;

    /// <summary>Starts listening and the snapshot timer.</summary>
    /// <param name="directory">Snapshot directory; defaults to <c>%APPDATA%\HtpcMatterBridge\logs</c>. Pass a temp path in tests/demos.</param>
    /// <param name="interval">Snapshot period; defaults to 60 s. Shrinkable for tests.</param>
    public MetricsFileListener(string? directory = null, TimeSpan? interval = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HtpcMatterBridge",
            "logs");
        Prune();

        // Touch one instrument so AppMetrics' static initializer has run and
        // Start() publishes every instrument (zero-initialized) immediately.
        _ = AppMetrics.ActionExecuteMs;

        _listener = new MeterListener { InstrumentPublished = OnInstrumentPublished };
        _listener.SetMeasurementEventCallback<long>(OnLongMeasurement);
        _listener.SetMeasurementEventCallback<double>(OnDoubleMeasurement);
        _listener.Start();

        TimeSpan period = interval ?? TimeSpan.FromSeconds(60);
        _timer = new System.Threading.Timer(_ => Flush(), null, period, period);
    }

    /// <summary>The snapshot file the next flush appends to (one file per calendar day, like the app log).</summary>
    public string CurrentFilePath => Path.Combine(_directory, $"metrics-{DateTime.Now:yyyyMMdd}.jsonl");

    /// <summary>
    /// Appends one snapshot line now, unless nothing changed: a payload
    /// identical to the last line written to the same day's file is skipped
    /// (idle churn, S6-1). A new day (or a deleted file) always writes, so
    /// each day's file exists with at least one line. Called by the timer and
    /// the final dispose; safe to call anytime. Never throws.
    /// </summary>
    public void Flush()
    {
        try
        {
            lock (_gate)
            {
                string payload = BuildPayloadLocked();
                string path = CurrentFilePath;
                if (payload == _lastWrittenPayload
                    && string.Equals(path, _lastWrittenPath, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(path))
                {
                    return;
                }

                Directory.CreateDirectory(_directory);

                // The line is the payload object with "ts" prepended ("O" is
                // the same ISO-8601 shape Utf8JsonWriter emits for DateTime).
                string line = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{{\"ts\":\"{DateTime.UtcNow:O}\",{payload[1..]}");
                File.AppendAllLines(path, [line]);
                _lastWrittenPayload = payload;
                _lastWrittenPath = path;
            }
        }
        catch
        {
            // Metrics must never be the reason the app crashes.
        }
    }

    /// <summary>Stops the timer and listener, then writes the final snapshot. Idempotent.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _timer.Dispose();
        _listener.Dispose();
        Flush();
    }

    private void OnInstrumentPublished(Instrument instrument, MeterListener listener)
    {
        if (instrument.Meter.Name != AppMetrics.MeterName)
        {
            return;
        }

        lock (_gate)
        {
            // Zero-init so every instrument shows up in the very first
            // snapshot, measured or not.
            if (instrument is Counter<long>)
            {
                _ = _counters.TryAdd(instrument.Name, 0);
            }
            else if (instrument is Histogram<double>)
            {
                _ = _histograms.TryAdd(instrument.Name, new HistogramState());
            }
        }

        listener.EnableMeasurementEvents(instrument);
    }

    private void OnLongMeasurement(
        Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        lock (_gate)
        {
            _counters[instrument.Name] = _counters.GetValueOrDefault(instrument.Name) + measurement;
        }
    }

    private void OnDoubleMeasurement(
        Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        lock (_gate)
        {
            if (!_histograms.TryGetValue(instrument.Name, out HistogramState? histogram))
            {
                histogram = new HistogramState();
                _histograms[instrument.Name] = histogram;
            }

            histogram.Record(measurement);
        }
    }

    /// <summary>
    /// The timestamp-free snapshot body <c>{"counters":…,"histograms":…}</c> —
    /// deterministic for a given metric state, so string equality with the
    /// last written payload IS the "nothing changed" test. Caller must hold
    /// <c>_gate</c>.
    /// </summary>
    private string BuildPayloadLocked()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("counters");
            foreach ((string name, long value) in _counters)
            {
                writer.WriteNumber(name, value);
            }

            writer.WriteEndObject();
            writer.WriteStartObject("histograms");
            foreach ((string name, HistogramState histogram) in _histograms)
            {
                writer.WriteStartObject(name);
                writer.WriteNumber("count", histogram.Count);
                if (histogram.Count > 0)
                {
                    writer.WriteNumber("min", histogram.Min);
                    writer.WriteNumber("max", histogram.Max);
                    writer.WriteNumber("avg", histogram.Sum / histogram.Count);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private void Prune()
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (string path in Directory.EnumerateFiles(_directory, "metrics-*.jsonl"))
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
        }
        catch
        {
            // Best-effort housekeeping, same policy as Log.Initialize.
        }
    }

    /// <summary>Running histogram aggregate (count/sum/min/max). Guarded by the owning listener's <c>_gate</c>.</summary>
    private sealed class HistogramState
    {
        internal long Count { get; private set; }

        internal double Sum { get; private set; }

        internal double Min { get; private set; }

        internal double Max { get; private set; }

        internal void Record(double value)
        {
            Min = Count == 0 ? value : Math.Min(Min, value);
            Max = Count == 0 ? value : Math.Max(Max, value);
            Count++;
            Sum += value;
        }
    }
}
