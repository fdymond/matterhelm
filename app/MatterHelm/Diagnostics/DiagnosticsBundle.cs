using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MatterHelm.Diagnostics;

/// <summary>
/// Builds the local-only diagnostics zip (ADR-006 §2): the app's daily logs
/// and metrics snapshots, an environment manifest, and <c>config.json</c>.
/// Nothing ever uploads. Privacy rules the manifest must keep: no machine
/// name, no username, no user-profile paths (it therefore contains no file
/// paths at all); the IPC token is runtime-only — never logged, never
/// persisted — so it is unreachable from every input this bundle reads,
/// which the bundle token-scan test enforces.
/// </summary>
public static class DiagnosticsBundle
{
    /// <summary>The effective data root (<see cref="AppPaths.Root"/>), read per access so the S7-2 migration fallback applies.</summary>
    private static string AppDataDir => AppPaths.Root;

    /// <summary>The timestamped (UTC) file name a new export gets, e.g. <c>matterhelm-diagnostics-20260729-181500Z.zip</c>.</summary>
    public static string SuggestedFileName() =>
        $"matterhelm-diagnostics-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}Z.zip";

    /// <summary>
    /// Produces <c>matterhelm-diagnostics-&lt;utcstamp&gt;.zip</c> in
    /// <paramref name="directory"/> (default <c>%APPDATA%\MatterHelm</c>)
    /// from the real log directory and config. Returns the zip's full path.
    /// </summary>
    public static string Export(string? directory = null) =>
        ExportTo(Path.Combine(directory ?? AppDataDir, SuggestedFileName()));

    /// <summary>
    /// Produces the bundle at exactly <paramref name="zipPath"/>.
    /// <paramref name="logsDirectory"/>/<paramref name="configPath"/> default
    /// to the real locations and are injectable so tests and demos bundle
    /// temp directories instead of the user profile.
    /// </summary>
    public static string ExportTo(string zipPath, string? logsDirectory = null, string? configPath = null)
    {
        string logsDir = logsDirectory ?? Path.Combine(AppDataDir, "logs");
        string config = configPath ?? MatterHelm.Config.DefaultPath;

        string? parent = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        using ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        WriteManifest(archive);
        if (File.Exists(config))
        {
            // Documented carve-out (S5-R F3): config.json is included VERBATIM.
            // It is user-authored and may contain user-chosen paths (e.g. a
            // custom launch command under C:\Users\<name>\...) — the
            // username-scrubbing guarantee applies to the manifest and logs,
            // not to content the user wrote into their own config.
            AddFile(archive, config, "config.json");
        }

        if (Directory.Exists(logsDir))
        {
            foreach (string pattern in (string[])["app-*.log", "metrics-*.jsonl"])
            {
                foreach (string file in Directory.EnumerateFiles(logsDir, pattern).Order(StringComparer.OrdinalIgnoreCase))
                {
                    AddRedactedTextFile(archive, file, "logs/" + Path.GetFileName(file));
                }
            }
        }

        return zipPath;
    }

    // S5-R F1: matter.js's Commissioning facility historically logged the raw
    // setup passcode / manual pairing code / QR payload, and those lines can
    // persist in app logs written BEFORE the bridge-side suppression landed
    // (or with a user override re-enabling that facility). Scrub commissioning
    // credentials from bundled logs regardless of how they got there.
    private static readonly (string Pattern, string Replacement)[] LogRedactions =
    [
        (@"passcode:\s*\d+", "passcode: [redacted]"),
        (@"manual pairing code:\s*\d+", "manual pairing code: [redacted]"),
        (@"MT:[A-Z0-9.\-]{5,}", "MT:[redacted]"),
    ];

    private static void AddRedactedTextFile(ZipArchive archive, string path, string entryName)
    {
        // FileShare.ReadWrite: the file may gain lines mid-export — a snapshot
        // mid-append is fine.
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(source);
        string text = reader.ReadToEnd();
        foreach ((string pattern, string replacement) in LogRedactions)
        {
            text = System.Text.RegularExpressions.Regex.Replace(text, pattern, replacement);
        }

        ZipArchiveEntry entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }

    private static void WriteManifest(ZipArchive archive)
    {
        ZipArchiveEntry entry = archive.CreateEntry("manifest.json");
        using Stream stream = entry.Open();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("generatedUtc", DateTime.UtcNow);
        writer.WriteString("os", Environment.OSVersion.VersionString);
        writer.WriteString("dotnetRuntime", RuntimeInformation.FrameworkDescription);
        writer.WriteString(
            "appVersion", typeof(DiagnosticsBundle).Assembly.GetName().Version?.ToString() ?? "unknown");
        string? sidecarVersion = TryReadSidecarVersion();
        if (sidecarVersion is null)
        {
            writer.WriteNull("sidecarVersion");
        }
        else
        {
            writer.WriteString("sidecarVersion", sidecarVersion);
        }

        writer.WriteString("locale", CultureInfo.CurrentCulture.Name);
        writer.WriteEndObject();
    }

    /// <summary>The packaged sidecar's version when cheaply known (<c>sidecar\package.json</c> next to the exe); null otherwise.</summary>
    private static string? TryReadSidecarVersion()
    {
        try
        {
            string packageJson = Path.Combine(AppContext.BaseDirectory, "sidecar", "package.json");
            if (!File.Exists(packageJson))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(packageJson));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("version", out JsonElement version)
                && version.ValueKind == JsonValueKind.String
                    ? version.GetString()
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void AddFile(ZipArchive archive, string path, string entryName)
    {
        // FileShare.ReadWrite: the app log / metrics file may gain lines while
        // the bundle is being built — a snapshot mid-append is fine.
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        ZipArchiveEntry entry = archive.CreateEntry(entryName);
        using Stream target = entry.Open();
        source.CopyTo(target);
    }
}
