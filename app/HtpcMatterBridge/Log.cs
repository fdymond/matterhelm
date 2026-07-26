namespace HtpcMatterBridge;

/// <summary>
/// Minimal rolling daily file logger. Writes one file per calendar day under
/// <c>%APPDATA%\HtpcMatterBridge\logs\app-yyyyMMdd.log</c> and prunes files
/// older than 7 days on startup. Never throws out of its public methods —
/// logging failures must not take down the tray app.
/// </summary>
public static class Log
{
    private const int RetentionDays = 7;
    private static readonly object _gate = new();
    private static readonly string _logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HtpcMatterBridge",
        "logs");

    /// <summary>Ensures the log directory exists and deletes logs older than <see cref="RetentionDays"/> days.</summary>
    public static void Initialize()
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_logDir);
                DateTime cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
                foreach (string path in Directory.EnumerateFiles(_logDir, "app-*.log"))
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                    }
                }
            }
        }
        catch
        {
            // Best-effort housekeeping; a stale log file is not worth crashing over.
        }
    }

    /// <summary>Writes an informational line.</summary>
    public static void Info(string message) => Write("INFO", message);

    /// <summary>Writes a warning line.</summary>
    public static void Warn(string message) => Write("WARN", message);

    /// <summary>Writes an error line.</summary>
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            string path = Path.Combine(_logDir, $"app-{DateTime.Now:yyyyMMdd}.log");
            lock (_gate)
            {
                Directory.CreateDirectory(_logDir);
                File.AppendAllLines(path, new[] { line });
            }
        }
        catch
        {
            // Logging must never be the reason the app crashes.
        }
    }
}
