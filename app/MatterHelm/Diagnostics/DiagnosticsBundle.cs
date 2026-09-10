using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MatterHelm.Diagnostics;

/// <summary>
/// Builds the local-only diagnostics zip (ADR-006 §2): the app's daily logs
/// and metrics snapshots, an environment manifest, and a sanitised
/// <c>config.json</c> unless the caller explicitly opts into raw config.
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
    public static string Export(string? directory = null, bool includeRawConfig = false) =>
        ExportTo(Path.Combine(directory ?? AppDataDir, SuggestedFileName()), includeRawConfig: includeRawConfig);

    /// <summary>
    /// Produces the bundle at exactly <paramref name="zipPath"/>.
    /// <paramref name="logsDirectory"/>/<paramref name="configPath"/> default
    /// to the real locations and are injectable so tests and demos bundle
    /// temp directories instead of the user profile.
    /// <paramref name="includeRawConfig"/> is an explicit privacy opt-in and
    /// defaults to <see langword="false"/>.
    /// </summary>
    public static string ExportTo(
        string zipPath,
        string? logsDirectory = null,
        string? configPath = null,
        bool includeRawConfig = false)
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
            if (includeRawConfig)
            {
                AddFile(archive, config, "config.json");
            }
            else
            {
                AddSanitizedConfig(archive, config);
            }
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

    private static readonly Regex PairingCodeRegex = new(
        @"(?:(?<label>\b(?:setup[\s_.-]*passcode|passcode|manual[\s_.-]*(?:pairing[\s_.-]*)?code)\b\s*[:=]\s*['""]?)\d(?:[\s-]*\d){5,}|(?<label>\bdiscriminator\b['""]?\s*[:=]\s*['""]?)\d{1,5})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex QrPayloadRegex = new(
        @"(?:MT:|MT%3A)[A-Z0-9.%\-]{5,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex QrBlockArtRegex = new(
        @"[\u2580-\u259F]+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex QrBlockArtLineRegex = new(
        @"^(?:(?=[\s\u2580-\u259F]*[\u2580-\u259F])[\s\u2580-\u259F]+|.*[\u2580-\u259F]{8,}.*)$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex Ipv4CandidateRegex = new(
        @"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex Ipv6CandidateRegex = new(
        @"(?<![0-9A-Fa-f:.%])(?=[0-9A-Fa-f:.%]*:)[0-9A-Fa-f:.]*[0-9A-Fa-f](?:%[0-9A-Za-z_.-]+)?(?![0-9A-Fa-f:.%])",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static void AddRedactedTextFile(ZipArchive archive, string path, string entryName)
    {
        // FileShare.ReadWrite: the file may gain lines mid-export — a snapshot
        // mid-append is fine.
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(source);
        ZipArchiveEntry entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        while (reader.ReadLine() is { } line)
        {
            writer.WriteLine(RedactLogLine(line));
        }
    }

    private static void AddSanitizedConfig(ZipArchive archive, string path)
    {
        ZipArchiveEntry entry = archive.CreateEntry("config.json");
        using Stream target = entry.Open();
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using JsonDocument document = JsonDocument.Parse(source);
            using var writer = new Utf8JsonWriter(target, new JsonWriterOptions { Indented = true });
            WriteSanitizedConfigElement(writer, document.RootElement, propertyName: null);
        }
        catch (JsonException)
        {
            using var writer = new Utf8JsonWriter(target, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteString("error", "config.json was invalid and could not be sanitised");
            writer.WriteEndObject();
        }
    }

    private static void WriteSanitizedConfigElement(Utf8JsonWriter writer, JsonElement element, string? propertyName)
    {
        if (propertyName is not null && propertyName.Equals("uniqueIdSeed", StringComparison.OrdinalIgnoreCase))
        {
            writer.WriteStringValue("<redacted>");
            return;
        }

        if (propertyName is not null && propertyName.Equals("args", StringComparison.OrdinalIgnoreCase))
        {
            int length = element.ValueKind == JsonValueKind.String
                ? (element.GetString()?.Length ?? 0)
                : element.GetRawText().Length;
            writer.WriteStringValue($"<redacted: {length} chars>");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteSanitizedConfigElement(writer, property.Value, property.Name);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteSanitizedConfigElement(writer, item, propertyName: null);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactProfileRoot(element.GetString() ?? ""));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string RedactLogLine(string line)
    {
        string redacted = PairingCodeRegex.Replace(line, "${label}<redacted>");
        redacted = QrPayloadRegex.Replace(redacted, match =>
            match.Value.StartsWith("MT%3A", StringComparison.OrdinalIgnoreCase)
                ? "MT%3A<redacted>"
                : "MT:<redacted>");
        if (QrBlockArtLineRegex.IsMatch(redacted))
        {
            redacted = "<qr-art>";
        }
        if (redacted.Contains("MT:<redacted>", StringComparison.OrdinalIgnoreCase)
            || redacted.Contains("MT%3A<redacted>", StringComparison.OrdinalIgnoreCase))
        {
            redacted = QrBlockArtRegex.Replace(redacted, "<qr-art>");
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        redacted = ReplaceLiteral(redacted, profile, "<profile>");
        redacted = ReplaceLiteral(redacted, profile.Replace('\\', '/'), "<profile>");
        redacted = ReplaceIdentityLiteral(redacted, Environment.UserName, "<user>");
        redacted = ReplaceIdentityLiteral(redacted, Environment.MachineName, "<machine>");
        redacted = Ipv4CandidateRegex.Replace(redacted, RedactIpLiteral);
        return Ipv6CandidateRegex.Replace(redacted, RedactIpLiteral);
    }

    private static string RedactProfileRoot(string value)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string redacted = ReplaceLiteral(value, profile, "%USERPROFILE%");
        redacted = ReplaceLiteral(redacted, profile.Replace('\\', '/'), "%USERPROFILE%");
        return ReplaceIdentityLiteral(redacted, Environment.UserName, "%USERNAME%");
    }

    private static string ReplaceLiteral(string value, string sensitive, string replacement) =>
        string.IsNullOrEmpty(sensitive)
            ? value
            : Regex.Replace(
                value,
                Regex.Escape(sensitive),
                _ => replacement,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));

    internal static string ReplaceIdentityLiteral(string value, string sensitive, string replacement) =>
        string.IsNullOrEmpty(sensitive)
            ? value
            : Regex.Replace(
                value,
                $@"(?<![A-Za-z0-9_]){Regex.Escape(sensitive)}(?![A-Za-z0-9_])",
                _ => replacement,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));

    private static string RedactIpLiteral(Match match) =>
        IPAddress.TryParse(match.Value, out _) ? "<ip>" : match.Value;

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
