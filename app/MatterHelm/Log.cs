using System.Text;

namespace MatterHelm;

/// <summary>
/// Severity levels for the app's own log (ADR-006 §2). Declaration order is
/// significance order — <see cref="Log"/> gates by comparing ranks.
/// </summary>
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
/// Minimal rolling daily file logger. Writes one file per calendar day under
/// <c>%APPDATA%\MatterHelm\logs\app-yyyyMMdd.log</c> and prunes files
/// older than 7 days on startup and each date rollover. A daily file rolls to
/// numbered segments at 5 MiB, bounding any one append target. Lines below <see cref="MinimumLevel"/>
/// (config <c>appLogLevel</c>, applied live — ADR-006 §2) are dropped. Never
/// throws out of its public methods — logging failures must not take down the
/// tray app. Full Microsoft.Extensions.Logging migration is deliberately
/// deferred (ADR-006 §2); this stays a static logger.
/// </summary>
public static class Log
{
    private const int RetentionDays = 7;
    private const long DefaultFileSizeLimitBytes = 5 * 1024 * 1024;
    private static readonly Lock _gate = new();
    private static int _minimumLevel = (int)LogLevel.Info;
    private static string? _directory;
    private static DateOnly? _lastPrunedLocalDate;

    internal static TimeProvider Clock { get; set; } = TimeProvider.System;

    internal static long FileSizeLimitBytes { get; set; } = DefaultFileSizeLimitBytes;

    /// <summary>
    /// Minimum level a line must have to be written. Thread-safe; applies to
    /// the very next write (live, no restart). <c>Program</c> keeps it in
    /// sync with the config's <c>appLogLevel</c>.
    /// </summary>
    public static LogLevel MinimumLevel
    {
        get => (LogLevel)Volatile.Read(ref _minimumLevel);
        set => Volatile.Write(ref _minimumLevel, (int)value);
    }

    /// <summary>
    /// The directory log files land in (default
    /// <c>%APPDATA%\MatterHelm\logs</c>, derived from <see cref="AppPaths.Root"/>
    /// per access so the S7-2 migration fallback applies). Settable for
    /// tests/demos only, so they never touch the real user profile; production
    /// leaves it alone.
    /// </summary>
    public static string LogDirectory
    {
        get => Volatile.Read(ref _directory) ?? Path.Combine(AppPaths.Root, "logs");
        set
        {
            lock (_gate)
            {
                Volatile.Write(ref _directory, value);
                _lastPrunedLocalDate = null;
            }
        }
    }

    /// <summary>True iff a line at <paramref name="level"/> would currently be written.</summary>
    public static bool IsEnabled(LogLevel level) => level >= MinimumLevel;

    /// <summary>
    /// Maps a config <c>appLogLevel</c> string to a level. Unknown values map
    /// to <see cref="LogLevel.Info"/> — <see cref="Config"/> validates the
    /// field upstream, so this never needs to reject.
    /// </summary>
    public static LogLevel ParseLevel(string value) => value switch
    {
        "debug" => LogLevel.Debug,
        "warn" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => LogLevel.Info,
    };

    /// <summary>Ensures the log directory exists and deletes logs older than <see cref="RetentionDays"/> days.</summary>
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
            }
        }
        catch
        {
            // Best-effort housekeeping; a stale log file is not worth crashing over.
        }
    }

    /// <summary>Writes a debug line (suppressed unless <see cref="MinimumLevel"/> is <see cref="LogLevel.Debug"/>).</summary>
    public static void Debug(string message) => Write(LogLevel.Debug, "DEBUG", message);

    /// <summary>Writes an informational line.</summary>
    public static void Info(string message) => Write(LogLevel.Info, "INFO", message);

    /// <summary>Writes a warning line.</summary>
    public static void Warn(string message) => Write(LogLevel.Warn, "WARN", message);

    /// <summary>Writes an error line.</summary>
    public static void Error(string message) => Write(LogLevel.Error, "ERROR", message);

    private static void Write(LogLevel level, string label, string message)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        try
        {
            DateTimeOffset localNow = Clock.GetLocalNow();
            string line = $"{localNow:yyyy-MM-dd HH:mm:ss.fff} [{label}] {message}";
            string dir = LogDirectory;
            lock (_gate)
            {
                Directory.CreateDirectory(dir);
                DateOnly localDate = DateOnly.FromDateTime(localNow.DateTime);
                if (_lastPrunedLocalDate != localDate)
                {
                    PruneLocked(dir, Clock.GetUtcNow().UtcDateTime);
                    _lastPrunedLocalDate = localDate;
                }

                int lineBytes = Encoding.UTF8.GetByteCount(line + Environment.NewLine);
                string path = SelectWritablePath(dir, localNow, lineBytes);
                File.AppendAllLines(path, [line]);
            }
        }
        catch
        {
            // Logging must never be the reason the app crashes.
        }
    }

    private static string SelectWritablePath(string directory, DateTimeOffset localNow, int incomingBytes)
    {
        string stem = $"app-{localNow:yyyyMMdd}";
        for (int segment = 0; ; segment++)
        {
            string suffix = segment == 0 ? string.Empty : $"-{segment:000}";
            string path = Path.Combine(directory, $"{stem}{suffix}.log");
            if (!File.Exists(path) || new FileInfo(path).Length + incomingBytes <= FileSizeLimitBytes)
            {
                return path;
            }
        }
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
