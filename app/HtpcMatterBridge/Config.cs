using System.Text.Json;
using System.Text.Json.Serialization;

namespace HtpcMatterBridge;

/// <summary>Display names for the five Matter endpoints — the Google voice targets (BLUEPRINT §2.2).</summary>
public sealed class DeviceNamesConfig
{
    /// <summary>Speaker endpoint name (OnOff = mute, LevelControl = volume).</summary>
    public string Speaker { get; set; } = "HTPC Speaker";

    /// <summary>Momentary play/pause endpoint name.</summary>
    public string PlayPause { get; set; } = "HTPC Play Pause";

    /// <summary>Momentary next-track endpoint name.</summary>
    public string Next { get; set; } = "HTPC Next";

    /// <summary>Momentary previous-track endpoint name.</summary>
    public string Previous { get; set; } = "HTPC Previous";

    /// <summary>Stateful power endpoint name.</summary>
    public string Power { get; set; } = "HTPC Power";
}

/// <summary>What the stateful power endpoint does on an "off" write (BLUEPRINT §2.2 power row).</summary>
public enum PowerOffAction
{
    /// <summary>Turn displays off only.</summary>
    DisplaysOff,

    /// <summary>Pause media, then turn displays off (default).</summary>
    PauseAndDisplaysOff,

    /// <summary>Suspend the machine.</summary>
    Sleep,
}

/// <summary>
/// The full <c>config.json</c> document (BLUEPRINT §2.3/§2.4). Every property
/// has a spec-mandated default so a freshly-constructed instance is already a
/// valid config.
/// </summary>
public sealed class BridgeConfig
{
    /// <summary>The five endpoint display names.</summary>
    public DeviceNamesConfig DeviceNames { get; set; } = new();

    /// <summary>Loopback port the tray app's <c>IpcServer</c> listens on.</summary>
    public int IpcPort { get; set; } = 39531;

    /// <summary>What the stateful power endpoint's "off" write does.</summary>
    public PowerOffAction PowerOffAction { get; set; } = PowerOffAction.PauseAndDisplaysOff;

    /// <summary>Whether the overlay HUD flashes on commands.</summary>
    public bool OverlayEnabled { get; set; } = true;

    /// <summary>mDNS interface pin for multi-NIC hosts (maps to matter.js <c>mdns.networkInterface</c>); <c>null</c> = auto-detect.</summary>
    public string? MdnsInterface { get; set; }

    /// <summary>pino log level handed to the sidecar.</summary>
    public string LogLevel { get; set; } = "info";
}

/// <summary>Payload for <see cref="Config.Changed"/>: the config before and after a load/reload.</summary>
public sealed class ConfigChangedEventArgs : EventArgs
{
    /// <summary>Creates the event payload.</summary>
    public ConfigChangedEventArgs(BridgeConfig oldConfig, BridgeConfig newConfig)
    {
        OldConfig = oldConfig;
        NewConfig = newConfig;
    }

    /// <summary>The config in effect immediately before this load/reload.</summary>
    public BridgeConfig OldConfig { get; }

    /// <summary>The config now in effect.</summary>
    public BridgeConfig NewConfig { get; }
}

/// <summary>
/// Loads, validates, and persists <c>config.json</c>
/// (default <c>%APPDATA%\HtpcMatterBridge\config.json</c>, camelCase JSON,
/// BLUEPRINT §2.4). A missing file is created from defaults on first load.
/// A malformed file or malformed individual fields never crash the app: each
/// bad field falls back to its default and is logged once as a WARN; only the
/// bad fields are replaced, so a partially-valid file still loads its valid
/// fields. The file path is injectable so tests target a temp directory
/// instead of the real user profile.
/// </summary>
public sealed class Config
{
    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // Options-level (not attribute-level) so the camelCase naming policy
        // applies to enum member names too ("pauseAndDisplaysOff", not
        // "PauseAndDisplaysOff") — the wire format the file schema promises.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly Action<string, string> _log;

    /// <summary>
    /// Loads (or creates) the config at <paramref name="path"/>, defaulting to
    /// <c>%APPDATA%\HtpcMatterBridge\config.json</c>.
    /// </summary>
    /// <param name="path">Config file path; pass a temp-directory path in tests — never the real profile.</param>
    /// <param name="log">Log sink (level, message); defaults to <see cref="HtpcMatterBridge.Log"/>. Injectable for tests.</param>
    public Config(string? path = null, Action<string, string>? log = null)
    {
        _path = path ?? DefaultPath;
        _log = log ?? DefaultLog;
        Current = LoadOrCreate();
    }

    /// <summary>The default config path: <c>%APPDATA%\HtpcMatterBridge\config.json</c>.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HtpcMatterBridge",
        "config.json");

    /// <summary>The config currently in effect. Replaced (not mutated in place) by <see cref="Reload"/>.</summary>
    public BridgeConfig Current { get; private set; }

    /// <summary>
    /// Raised after <see cref="Reload"/> re-reads the file, with the old and
    /// new config. Fires synchronously on the caller's thread — reload is a
    /// user-initiated, UI-thread action (tray "Reload config" menu item; S2-5
    /// wires the subscriber), so no thread marshalling is needed here.
    /// </summary>
    public event EventHandler<ConfigChangedEventArgs>? Changed;

    /// <summary>Writes <see cref="Current"/> to disk (creating the parent directory if needed).</summary>
    public void Save() => WriteFile(Current);

    /// <summary>
    /// Re-reads the file from disk (same malformed-field resilience as the
    /// initial load) and raises <see cref="Changed"/> with the old and new
    /// config. Applies without an app restart.
    /// </summary>
    public void Reload()
    {
        BridgeConfig previous = Current;
        BridgeConfig updated = LoadOrCreate();
        Current = updated;
        Changed?.Invoke(this, new ConfigChangedEventArgs(previous, updated));
    }

    private BridgeConfig LoadOrCreate()
    {
        string? text = TryReadFile();
        if (text is null)
        {
            var defaults = new BridgeConfig();
            WriteFile(defaults);
            return defaults;
        }

        return ParseWithFallback(text);
    }

    private string? TryReadFile()
    {
        try
        {
            return File.Exists(_path) ? File.ReadAllText(_path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log("WARN", $"config.json could not be read ({ex.Message}); using defaults.");
            return null;
        }
    }

    private void WriteFile(BridgeConfig config)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(config, _writeOptions);

            // Write-then-move: a crash mid-write leaves the previous file intact
            // rather than a half-written config.json.
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log("ERROR", $"config.json could not be written ({ex.Message}).");
        }
    }

    /// <summary>Parses <paramref name="json"/> field-by-field: an invalid field falls back to its default and logs a WARN; the rest of the document still applies.</summary>
    private BridgeConfig ParseWithFallback(string json)
    {
        var result = new BridgeConfig();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            _log("WARN", $"config.json is not valid JSON ({ex.Message}); using defaults.");
            return result;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _log("WARN", "config.json root is not an object; using defaults.");
                return result;
            }

            ApplyDeviceNames(root, result.DeviceNames);
            ApplyIpcPort(root, result);
            ApplyPowerOffAction(root, result);
            ApplyOverlayEnabled(root, result);
            ApplyMdnsInterface(root, result);
            ApplyLogLevel(root, result);
        }

        return result;
    }

    private void ApplyDeviceNames(JsonElement root, DeviceNamesConfig names)
    {
        if (!root.TryGetProperty("deviceNames", out JsonElement deviceNames))
        {
            return;
        }

        if (deviceNames.ValueKind != JsonValueKind.Object)
        {
            _log("WARN", "config.json \"deviceNames\" is not an object; using default device names.");
            return;
        }

        names.Speaker = NameOrDefault(deviceNames, "speaker", names.Speaker);
        names.PlayPause = NameOrDefault(deviceNames, "playPause", names.PlayPause);
        names.Next = NameOrDefault(deviceNames, "next", names.Next);
        names.Previous = NameOrDefault(deviceNames, "previous", names.Previous);
        names.Power = NameOrDefault(deviceNames, "power", names.Power);
    }

    /// <summary>Reads one device-name field, falling back to <paramref name="defaultValue"/> for a missing, non-string, or empty value.</summary>
    private string NameOrDefault(JsonElement deviceNames, string field, string defaultValue)
    {
        if (!deviceNames.TryGetProperty(field, out JsonElement element))
        {
            return defaultValue;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            _log("WARN", $"config.json \"deviceNames.{field}\" is not a string; using default \"{defaultValue}\".");
            return defaultValue;
        }

        string value = element.GetString() ?? "";
        if (value.Length == 0)
        {
            _log("WARN", $"config.json \"deviceNames.{field}\" is empty; using default \"{defaultValue}\".");
            return defaultValue;
        }

        return value;
    }

    private void ApplyIpcPort(JsonElement root, BridgeConfig result)
    {
        if (!root.TryGetProperty("ipcPort", out JsonElement element))
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int port) && port is > 0 and <= 65535)
        {
            result.IpcPort = port;
            return;
        }

        _log("WARN", $"config.json \"ipcPort\" must be an integer 1-65535; using default {result.IpcPort}.");
    }

    private void ApplyPowerOffAction(JsonElement root, BridgeConfig result)
    {
        if (!root.TryGetProperty("powerOffAction", out JsonElement element))
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            switch (element.GetString())
            {
                case "displaysOff":
                    result.PowerOffAction = PowerOffAction.DisplaysOff;
                    return;
                case "pauseAndDisplaysOff":
                    result.PowerOffAction = PowerOffAction.PauseAndDisplaysOff;
                    return;
                case "sleep":
                    result.PowerOffAction = PowerOffAction.Sleep;
                    return;
            }
        }

        _log("WARN", $"config.json \"powerOffAction\" must be one of displaysOff/pauseAndDisplaysOff/sleep; using default {ToWireName(result.PowerOffAction)}.");
    }

    private void ApplyOverlayEnabled(JsonElement root, BridgeConfig result)
    {
        if (!root.TryGetProperty("overlayEnabled", out JsonElement element))
        {
            return;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            result.OverlayEnabled = element.GetBoolean();
            return;
        }

        _log("WARN", $"config.json \"overlayEnabled\" must be a boolean; using default {result.OverlayEnabled}.");
    }

    private void ApplyMdnsInterface(JsonElement root, BridgeConfig result)
    {
        if (!root.TryGetProperty("mdnsInterface", out JsonElement element))
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                result.MdnsInterface = null;
                return;
            case JsonValueKind.String:
                result.MdnsInterface = element.GetString();
                return;
            default:
                _log("WARN", "config.json \"mdnsInterface\" must be a string or null; using default (auto-detect).");
                return;
        }
    }

    private void ApplyLogLevel(JsonElement root, BridgeConfig result)
    {
        if (!root.TryGetProperty("logLevel", out JsonElement element))
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } value)
        {
            result.LogLevel = value;
            return;
        }

        _log("WARN", $"config.json \"logLevel\" must be a non-empty string; using default \"{result.LogLevel}\".");
    }

    private static string ToWireName(PowerOffAction action) => action switch
    {
        PowerOffAction.DisplaysOff => "displaysOff",
        PowerOffAction.PauseAndDisplaysOff => "pauseAndDisplaysOff",
        PowerOffAction.Sleep => "sleep",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    private static void DefaultLog(string level, string message)
    {
        switch (level)
        {
            case "ERROR":
                Log.Error(message);
                break;
            case "WARN":
                Log.Warn(message);
                break;
            default:
                Log.Info(message);
                break;
        }
    }
}
