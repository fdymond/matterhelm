using System.Text;

namespace MatterHelm;

/// <summary>Severity levels for the app's own log (ADR-006 §2).</summary>
public enum LogLevel
{
    /// <summary>Verbose diagnostics (per-action timing); suppressed by default.</summary>
    Debug,

    /// <summary>Normal operational events (the default minimum).</summary>
    Info,

    /// <summary>Something degraded but the app carries on.</summary>
    Warn,

    /// <summary>Something failed.</summary>
    Error,
}

/// <summary>
/// Minimal bounded daily file logger. It retains seven days, caps each day at
/// twenty 5 MiB segments, and rate-limits identical warning/error bursts.
/// </summary>
public static class Log
{
    private const int RetentionDays = 7;
    private const int RepeatLineLimit = 20;
    private const int DefaultSegmentsPerDayLimit = 20;
    private const long DefaultFileSizeLimitBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SegmentDeleteRetryWindow = TimeSpan.FromSeconds(30);
    private static readonly Lock _gate = new();
    private static int _minimumLevel = (int)LogLevel.Info;
    private static string? _directory;
    private static DateOnly? _lastPrunedLocalDate;
    private static string? _activeDirectory;
    private static DateOnly? _activeLocalDate;
    private static string? _activePath;
    private static long _activeLengthBytes;
    private static string? _repeatKey;
    private static string? _repeatMessage;
    private static string? _repeatLabel;
    private static DateTimeOffset _repeatWindowStart;
    private static int _repeatWritten;
    private static int _repeatSuppressed;
    private static int _segmentScanCount;
    private static int _segmentDeleteAttemptCount;
    private static DateTimeOffset _segmentDeleteRetryAfterUtc;

    internal static TimeProvider Clock { get; set; } = TimeProvider.System;

    internal static long FileSizeLimitBytes { get; set; } = DefaultFileSizeLimitBytes;

    internal static int SegmentsPerDayLimit { get; set; } = DefaultSegmentsPerDayLimit;

    internal static int SegmentScanCount => Volatile.Read(ref _segmentScanCount);

    internal static int SegmentDeleteAttemptCount => Volatile.Read(ref _segmentDeleteAttemptCount);

    /// <summary>Minimum severity written by the app logger.</summary>
    public static LogLevel MinimumLevel
    {
        get => (LogLevel)Volatile.Read(ref _minimumLevel);
        set => Volatile.Write(ref _minimumLevel, (int)value);
    }

    /// <summary>The directory log files land in; settable so tests never touch the user profile.</summary>
    public static string LogDirectory
    {
        get => Volatile.Read(ref _directory) ?? Path.Combine(AppPaths.Root, "logs");
        set
        {
            lock (_gate)
            {
                Volatile.Write(ref _directory, value);
                _lastPrunedLocalDate = null;
                ResetActiveSegmentLocked();
                ResetRepeatStateLocked();
                Volatile.Write(ref _segmentScanCount, 0);
                Volatile.Write(ref _segmentDeleteAttemptCount, 0);
            }
        }
    }

    /// <summary>True iff a line at <paramref name="level"/> would currently be written.</summary>
    public static bool IsEnabled(LogLevel level) => level >= MinimumLevel;

    /// <summary>Maps a config log-level string to a level; unknown values map to Info.</summary>
    public static LogLevel ParseLevel(string value) => value switch
    {
        "debug" => LogLevel.Debug,
        "warn" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => LogLevel.Info,
    };

    /// <summary>Ensures the log directory exists and deletes logs older than seven days.</summary>
    public static void Initialize()
    {
        try
        {
            lock (_gate)
            {
                string dir = LogDirectory;
                Directory.CreateDirectory(dir);
                DateTimeOffset localNow = Clock.GetLocalNow();
                PruneLocked(dir, Clock.GetUtcNow().UtcDateTime);
                _lastPrunedLocalDate = DateOnly.FromDateTime(localNow.DateTime);
                ResetActiveSegmentLocked();
            }
        }
        catch
        {
            // Best-effort housekeeping; a stale log file is not worth crashing over.
        }
    }

    /// <summary>Writes a debug line when debug logging is enabled.</summary>
    public static void Debug(string message) => WriteCore(LogLevel.Debug, "DEBUG", message);

    /// <summary>Writes an informational line.</summary>
    public static void Info(string message) => WriteCore(LogLevel.Info, "INFO", message);

    /// <summary>Writes a warning line.</summary>
    public static void Warn(string message) => WriteCore(LogLevel.Warn, "WARN", message);

    /// <summary>Writes an error line.</summary>
    public static void Error(string message) => WriteCore(LogLevel.Error, "ERROR", message);

    /// <summary>Writes any pending warning/error repeat summary, including during orderly shutdown.</summary>
    public static void Flush()
    {
        try
        {
            DateTimeOffset localNow = Clock.GetLocalNow();
            string dir = LogDirectory;
            lock (_gate)
            {
                string? summary = SuppressedSummaryLocked(localNow);
                if (summary is not null)
                {
                    Directory.CreateDirectory(dir);
                    AppendLineLocked(dir, localNow, summary);
                }

                ResetRepeatStateLocked();
            }
        }
        catch
        {
            // Logging must never be the reason the app crashes during shutdown.
        }
    }

    /// <summary>Adapts the string levels used by injectable component log sinks.</summary>
    internal static void Write(string level, string message)
    {
        switch (level)
        {
            case "DEBUG":
                Debug(message);
                break;
            case "WARN":
                Warn(message);
                break;
            case "ERROR":
                Error(message);
                break;
            default:
                Warn(message);
                break;
        }
    }

    private static void WriteCore(LogLevel level, string label, string message)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        try
        {
            DateTimeOffset localNow = Clock.GetLocalNow();
            string dir = LogDirectory;
            lock (_gate)
            {
                Directory.CreateDirectory(dir);
                DateOnly localDate = DateOnly.FromDateTime(localNow.DateTime);
                if (_lastPrunedLocalDate != localDate)
                {
                    string? rolloverSummary = SuppressedSummaryLocked(localNow);
                    if (rolloverSummary is not null)
                    {
                        AppendLineLocked(dir, localNow, rolloverSummary);
                    }

                    ResetRepeatStateLocked();
                    PruneLocked(dir, Clock.GetUtcNow().UtcDateTime);
                    _lastPrunedLocalDate = localDate;
                    ResetActiveSegmentLocked();
                }

                string? repeatSummary = ApplyRepeatPolicyLocked(level, label, message, localNow);
                if (repeatSummary is not null)
                {
                    AppendLineLocked(dir, localNow, repeatSummary);
                }

                if (IsCurrentLineSuppressedLocked(level, label, message, localNow))
                {
                    return;
                }

                AppendLineLocked(dir, localNow, FormatLine(localNow, label, message));
            }
        }
        catch
        {
            // Logging must never be the reason the app crashes.
        }
    }

    private static string? ApplyRepeatPolicyLocked(
        LogLevel level,
        string label,
        string message,
        DateTimeOffset localNow)
    {
        bool rateLimited = level is LogLevel.Warn or LogLevel.Error;
        string key = $"{label}\0{message}";
        bool sameWindow = rateLimited
            && _repeatKey == key
            && localNow - _repeatWindowStart >= TimeSpan.Zero
            && localNow - _repeatWindowStart <= RepeatWindow;
        if (sameWindow)
        {
            if (_repeatWritten < RepeatLineLimit)
            {
                _repeatWritten++;
            }
            else
            {
                _repeatSuppressed++;
            }

            return null;
        }

        string? summary = SuppressedSummaryLocked(localNow);
        ResetRepeatStateLocked();
        if (rateLimited)
        {
            _repeatKey = key;
            _repeatMessage = message;
            _repeatLabel = label;
            _repeatWindowStart = localNow;
            _repeatWritten = 1;
        }

        return summary;
    }

    private static bool IsCurrentLineSuppressedLocked(
        LogLevel level,
        string label,
        string message,
        DateTimeOffset localNow) =>
        level is LogLevel.Warn or LogLevel.Error
        && _repeatKey == $"{label}\0{message}"
        && localNow - _repeatWindowStart >= TimeSpan.Zero
        && localNow - _repeatWindowStart <= RepeatWindow
        && _repeatSuppressed > 0;

    private static string? SuppressedSummaryLocked(DateTimeOffset localNow) =>
        _repeatSuppressed == 0
            ? null
            : FormatLine(
                localNow,
                _repeatLabel!,
                $"suppressed {_repeatSuppressed} repeats of: {_repeatMessage}");

    private static void ResetRepeatStateLocked()
    {
        _repeatKey = null;
        _repeatMessage = null;
        _repeatLabel = null;
        _repeatWindowStart = default;
        _repeatWritten = 0;
        _repeatSuppressed = 0;
    }

    private static string FormatLine(DateTimeOffset localNow, string label, string message) =>
        $"{localNow:yyyy-MM-dd HH:mm:ss.fff} [{label}] {message}";

    private static void AppendLineLocked(string directory, DateTimeOffset localNow, string line)
    {
        int lineBytes = Encoding.UTF8.GetByteCount(line + Environment.NewLine);
        string path = SelectWritablePathLocked(directory, localNow, lineBytes, out int evictedSegments);
        if (evictedSegments > 0)
        {
            string evictionLine = FormatLine(
                localNow,
                "WARN",
                $"log segment cap reached; evicted {evictedSegments} oldest segment{(evictedSegments == 1 ? "" : "s")} for {localNow:yyyy-MM-dd}.");
            File.AppendAllLines(path, [evictionLine]);
            _activeLengthBytes += Encoding.UTF8.GetByteCount(evictionLine + Environment.NewLine);
        }

        File.AppendAllLines(path, [line]);
        _activeLengthBytes += lineBytes;
    }

    private static string SelectWritablePathLocked(
        string directory,
        DateTimeOffset localNow,
        int incomingBytes,
        out int evictedSegments)
    {
        DateOnly localDate = DateOnly.FromDateTime(localNow.DateTime);
        if (_activePath is not null
            && _activeDirectory == directory
            && _activeLocalDate == localDate
            && File.Exists(_activePath)
            && (_activeLengthBytes + incomingBytes <= FileSizeLimitBytes
                || Clock.GetUtcNow() < _segmentDeleteRetryAfterUtc))
        {
            evictedSegments = 0;
            return _activePath;
        }

        Interlocked.Increment(ref _segmentScanCount);
        string stem = $"app-{localNow:yyyyMMdd}";
        List<string> paths = [.. Directory.EnumerateFiles(directory, $"{stem}*.log")];
        paths.Sort((left, right) =>
        {
            int byTime = File.GetLastWriteTimeUtc(left).CompareTo(File.GetLastWriteTimeUtc(right));
            return byTime != 0
                ? byTime
                : SegmentIndex(left, stem).CompareTo(SegmentIndex(right, stem));
        });

        evictedSegments = 0;
        while (paths.Count > SegmentsPerDayLimit)
        {
            string oldest = paths[0];
            if (!TryDeleteSegment(oldest))
            {
                return UseNewestSegmentDespiteCap(directory, localDate, paths);
            }

            paths.RemoveAt(0);
            evictedSegments++;
        }

        string? newest = paths.Count == 0 ? null : paths[^1];
        if (newest is not null)
        {
            long length = new FileInfo(newest).Length;
            if (length + incomingBytes <= FileSizeLimitBytes)
            {
                SetActiveSegmentLocked(directory, localDate, newest, length);
                return newest;
            }
        }

        while (paths.Count >= SegmentsPerDayLimit)
        {
            string oldest = paths[0];
            if (!TryDeleteSegment(oldest))
            {
                return UseNewestSegmentDespiteCap(directory, localDate, paths);
            }

            paths.RemoveAt(0);
            evictedSegments++;
        }

        string path = NextSegmentPath(directory, stem, paths);
        SetActiveSegmentLocked(directory, localDate, path, 0);
        return path;
    }

    private static bool TryDeleteSegment(string path)
    {
        Interlocked.Increment(ref _segmentDeleteAttemptCount);
        try
        {
            File.Delete(path);
            _segmentDeleteRetryAfterUtc = default;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _segmentDeleteRetryAfterUtc = Clock.GetUtcNow().Add(SegmentDeleteRetryWindow);
            return false;
        }
    }

    private static string UseNewestSegmentDespiteCap(
        string directory,
        DateOnly localDate,
        List<string> paths)
    {
        string newest = paths[^1];
        SetActiveSegmentLocked(directory, localDate, newest, new FileInfo(newest).Length);
        return newest;
    }

    private static string NextSegmentPath(string directory, string stem, List<string> paths)
    {
        int segment = paths
            .Select(path => SegmentIndex(path, stem))
            .Where(index => index != int.MaxValue)
            .DefaultIfEmpty(-1)
            .Max() + 1;
        while (true)
        {
            string suffix = segment == 0 ? string.Empty : $"-{segment:000}";
            string candidate = Path.Combine(directory, $"{stem}{suffix}.log");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            segment++;
        }
    }

    private static int SegmentIndex(string path, string stem)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (name == stem)
        {
            return 0;
        }

        if (name.Length <= stem.Length || name[stem.Length] != '-')
        {
            return int.MaxValue;
        }

        return int.TryParse(name.AsSpan(stem.Length + 1), out int segment)
            ? segment
            : int.MaxValue;
    }

    private static void SetActiveSegmentLocked(
        string directory,
        DateOnly localDate,
        string path,
        long length)
    {
        _activeDirectory = directory;
        _activeLocalDate = localDate;
        _activePath = path;
        _activeLengthBytes = length;
    }

    private static void ResetActiveSegmentLocked()
    {
        _activeDirectory = null;
        _activeLocalDate = null;
        _activePath = null;
        _activeLengthBytes = 0;
        _segmentDeleteRetryAfterUtc = default;
    }

    private static void PruneLocked(string directory, DateTime utcNow)
    {
        DateTime cutoff = utcNow.AddDays(-RetentionDays);
        foreach (string path in Directory.EnumerateFiles(directory, "app-*.log"))
        {
            if (File.GetLastWriteTimeUtc(path) < cutoff)
            {
                File.Delete(path);
            }
        }
    }
}
