using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HtpcMatterBridge.Diagnostics;

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
    private static readonly string _appDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HtpcMatterBridge");

    /// <summary>The timestamped (UTC) file name a new export gets, e.g. <c>htpc-diagnostics-20260729-181500Z.zip</c>.</summary>
    public static string SuggestedFileName() =>
        $"htpc-diagnostics-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}Z.zip";

    /// <summary>
    /// Produces <c>htpc-diagnostics-&lt;utcstamp&gt;.zip</c> in
    /// <paramref name="directory"/> (default <c>%APPDATA%\HtpcMatterBridge</c>)
    /// from the real log directory and config. Returns the zip's full path.
    /// </summary>
    public static string Export(string? directory = null) =>
        ExportTo(Path.Combine(directory ?? _appDataDir, SuggestedFileName()));

    /// <summary>
    /// Produces the bundle at exactly <paramref name="zipPath"/>.
    /// <paramref name="logsDirectory"/>/<paramref name="configPath"/> default
    /// to the real locations and are injectable so tests and demos bundle
    /// temp directories instead of the user profile.
    /// </summary>
    public static string ExportTo(string zipPath, string? logsDirectory = null, string? configPath = null)
    {
        string logsDir = logsDirectory ?? Path.Combine(_appDataDir, "logs");
        string config = configPath ?? HtpcMatterBridge.Config.DefaultPath;

        string? parent = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        using ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        WriteManifest(archive);
        if (File.Exists(config))
        {
            AddFile(archive, config, "config.json");
        }

        if (Directory.Exists(logsDir))
        {
            foreach (string pattern in (string[])["app-*.log", "metrics-*.jsonl"])
            {
                foreach (string file in Directory.EnumerateFiles(logsDir, pattern).Order(StringComparer.OrdinalIgnoreCase))
                {
                    AddFile(archive, file, "logs/" + Path.GetFileName(file));
                }
            }
        }

        return zipPath;
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
