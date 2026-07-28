using System.Text.Json;
using System.Text.Json.Serialization;

namespace HtpcMatterBridge;

/// <summary>One built-in command endpoint: display name (the Google voice target) + whether the bridge publishes it (ADR-004 §1).</summary>
public sealed class BuiltinCommandConfig
{
    /// <summary>Endpoint display name.</summary>
    public required string Name { get; set; }

    /// <summary>Whether the bridge publishes this endpoint.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>The media key a <c>mediaKey</c> custom action injects (ADR-004 §1).</summary>
public enum MediaKeyName
{
    /// <summary>Play/pause toggle.</summary>
    PlayPause,

    /// <summary>Next track.</summary>
    Next,

    /// <summary>Previous track.</summary>
    Previous,

    /// <summary>Media stop.</summary>
    Stop,

    /// <summary>System mute toggle.</summary>
    Mute,

    /// <summary>System volume up 5 %.</summary>
    VolumeUp,

    /// <summary>System volume down 5 %.</summary>
    VolumeDown,
}

/// <summary>
/// What a custom command does when its endpoint fires (ADR-004 §1). Executed
/// by the tray app only — the sidecar never sees actions. The wire form is
/// polymorphic on <c>type</c> (<c>mediaKey</c> | <c>launch</c>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MediaKeyActionConfig), "mediaKey")]
[JsonDerivedType(typeof(LaunchActionConfig), "launch")]
public abstract class CustomActionConfig;

/// <summary>Custom action injecting one media key (<c>{"type":"mediaKey","keyName":"stop"}</c>).</summary>
public sealed class MediaKeyActionConfig : CustomActionConfig
{
    /// <summary>Which key to inject.</summary>
    public required MediaKeyName KeyName { get; set; }
}

/// <summary>Custom action launching a program (<c>{"type":"launch","path":"...","args":"..."}</c>) — detached, never elevated, never via a shell.</summary>
public sealed class LaunchActionConfig : CustomActionConfig
{
    /// <summary>Absolute path of the executable to start.</summary>
    public required string Path { get; set; }

    /// <summary>Argument string, split per <c>Actions/AppLaunch.SplitArgs</c> (whitespace-separated, double quotes group).</summary>
    public string Args { get; set; } = "";
}

/// <summary>
/// One user-defined command, surfaced to Google Home as an additional
/// momentary endpoint (ADR-004 §1). <see cref="Key"/> is the stable
/// kebab-case slug identity (<see cref="CommandKey"/>) — renaming
/// <see cref="Name"/> never changes it, so renames need no re-pairing.
/// </summary>
public sealed class CustomCommandConfig
{
    /// <summary>Unique kebab-case slug; the Matter endpoint id and wire identifier.</summary>
    public required string Key { get; set; }

    /// <summary>Display name — the Google voice target.</summary>
    public required string Name { get; set; }

    /// <summary>Whether the bridge publishes this endpoint.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>What firing the endpoint does.</summary>
    public required CustomActionConfig Action { get; set; }
}

/// <summary>The <c>commands</c> section (ADR-004 §1): the five built-in endpoints plus the user's custom commands. Supersedes the pre-v2 <c>deviceNames</c> section.</summary>
public sealed class CommandsConfig
{
    /// <summary>Speaker endpoint (OnOff = mute, LevelControl = volume).</summary>
    public BuiltinCommandConfig Speaker { get; set; } = new() { Name = "HTPC Speaker" };

    /// <summary>Momentary play/pause endpoint.</summary>
    public BuiltinCommandConfig PlayPause { get; set; } = new() { Name = "HTPC Play Pause" };

    /// <summary>Momentary next-track endpoint.</summary>
    public BuiltinCommandConfig Next { get; set; } = new() { Name = "HTPC Next" };

    /// <summary>Momentary previous-track endpoint.</summary>
    public BuiltinCommandConfig Previous { get; set; } = new() { Name = "HTPC Previous" };

    /// <summary>Stateful power endpoint.</summary>
    public BuiltinCommandConfig Power { get; set; } = new() { Name = "HTPC Power" };

    /// <summary>Custom commands, in file order. Keys are unique (load drops duplicates).</summary>
    public List<CustomCommandConfig> Custom { get; set; } = [];
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
    /// <summary>Built-in and custom command endpoints (ADR-004 §1).</summary>
    public CommandsConfig Commands { get; set; } = new();

    /// <summary>Loopback port the tray app's <c>IpcServer</c> listens on.</summary>
    public int IpcPort { get; set; } = 39531;

    /// <summary>What the stateful power endpoint's "off" write does.</summary>
    public PowerOffAction PowerOffAction { get; set; } = PowerOffAction.PauseAndDisplaysOff;

    /// <summary>Whether the overlay HUD flashes on commands.</summary>
    public bool OverlayEnabled { get; set; } = true;

    /// <summary>
    /// Whether the bridge (sidecar + IPC server) runs — the tray "Enable
    /// bridge" checkbox, persisted. Defaults to <c>false</c>: on first run the
    /// user enables the bridge explicitly.
    /// </summary>
    public bool BridgeEnabled { get; set; }

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

        BridgeConfig config = ParseWithFallback(text, out bool migrated);
        if (migrated)
        {
            // ADR-004 §1: migrate-on-load, then persist the new shape so the
            // superseded "deviceNames" key disappears from the file.
            _log("INFO", "config.json migrated legacy \"deviceNames\" into the \"commands\" section.");
            WriteFile(config);
        }

        return config;
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

            // Source-generated contract (ADR-005): camelCase properties and
            // camelCase enum member names ("pauseAndDisplaysOff", not
            // "PauseAndDisplaysOff") — the wire format the file schema promises.
            string json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.BridgeConfig);

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

    /// <summary>
    /// Parses <paramref name="json"/> field-by-field: an invalid field falls
    /// back to its default and logs a WARN; the rest of the document still
    /// applies. <paramref name="migrated"/> is true iff a legacy
    /// <c>deviceNames</c> section was folded into <c>commands</c> (ADR-004
    /// §1) — the caller then rewrites the file in the new shape.
    /// </summary>
    private BridgeConfig ParseWithFallback(string json, out bool migrated)
    {
        var result = new BridgeConfig();
        migrated = false;

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

            migrated = ApplyCommands(root, result.Commands);
            ApplyIpcPort(root, result);
            ApplyPowerOffAction(root, result);
            ApplyOverlayEnabled(root, result);
            ApplyBridgeEnabled(root, result);
            ApplyMdnsInterface(root, result);
            ApplyLogLevel(root, result);
        }

        return result;
    }

    /// <summary>
    /// Applies the <c>commands</c> section, or — when it is absent but the
    /// superseded <c>deviceNames</c> section exists — migrates the legacy
    /// names into the built-ins (enabled, ADR-004 §1). Returns true iff the
    /// legacy path ran (the file must then be rewritten in the new shape).
    /// When both sections exist, <c>commands</c> wins and <c>deviceNames</c>
    /// is ignored like any other unknown root key.
    /// </summary>
    private bool ApplyCommands(JsonElement root, CommandsConfig commands)
    {
        if (root.TryGetProperty("commands", out JsonElement section))
        {
            if (section.ValueKind != JsonValueKind.Object)
            {
                _log("WARN", "config.json \"commands\" is not an object; using default commands.");
                return false;
            }

            ApplyBuiltinCommand(section, "speaker", commands.Speaker);
            ApplyBuiltinCommand(section, "playPause", commands.PlayPause);
            ApplyBuiltinCommand(section, "next", commands.Next);
            ApplyBuiltinCommand(section, "previous", commands.Previous);
            ApplyBuiltinCommand(section, "power", commands.Power);
            ApplyCustomCommands(section, commands);
            return false;
        }

        if (!root.TryGetProperty("deviceNames", out JsonElement deviceNames))
        {
            return false;
        }

        if (deviceNames.ValueKind != JsonValueKind.Object)
        {
            _log("WARN", "config.json \"deviceNames\" is not an object; using default device names.");
            return true; // Still migrated: the rewrite drops the dead key.
        }

        commands.Speaker.Name = LegacyNameOrDefault(deviceNames, "speaker", commands.Speaker.Name);
        commands.PlayPause.Name = LegacyNameOrDefault(deviceNames, "playPause", commands.PlayPause.Name);
        commands.Next.Name = LegacyNameOrDefault(deviceNames, "next", commands.Next.Name);
        commands.Previous.Name = LegacyNameOrDefault(deviceNames, "previous", commands.Previous.Name);
        commands.Power.Name = LegacyNameOrDefault(deviceNames, "power", commands.Power.Name);
        return true;
    }

    /// <summary>Applies one built-in command entry field-by-field (missing entry or field → keep defaults; wrong type → WARN + default).</summary>
    private void ApplyBuiltinCommand(JsonElement section, string field, BuiltinCommandConfig builtin)
    {
        if (!section.TryGetProperty(field, out JsonElement entry))
        {
            return;
        }

        if (entry.ValueKind != JsonValueKind.Object)
        {
            _log("WARN", $"config.json \"commands.{field}\" is not an object; using defaults for it.");
            return;
        }

        if (entry.TryGetProperty("name", out JsonElement name))
        {
            if (name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } value)
            {
                builtin.Name = value;
            }
            else
            {
                _log("WARN", $"config.json \"commands.{field}.name\" must be a non-empty string; using default \"{builtin.Name}\".");
            }
        }

        if (entry.TryGetProperty("enabled", out JsonElement enabled))
        {
            if (enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                builtin.Enabled = enabled.GetBoolean();
            }
            else
            {
                _log("WARN", $"config.json \"commands.{field}.enabled\" must be a boolean; using default {builtin.Enabled}.");
            }
        }
    }

    /// <summary>
    /// Applies <c>commands.custom</c>. Per ADR-004 §1 an entry with an
    /// invalid or duplicate <c>key</c> or an unusable <c>action</c> is
    /// DROPPED with one WARN (never half-loaded); merely cosmetic fields
    /// (<c>name</c>/<c>enabled</c>/<c>args</c>) fall back per field.
    /// </summary>
    private void ApplyCustomCommands(JsonElement section, CommandsConfig commands)
    {
        if (!section.TryGetProperty("custom", out JsonElement custom))
        {
            return;
        }

        if (custom.ValueKind != JsonValueKind.Array)
        {
            _log("WARN", "config.json \"commands.custom\" is not an array; using no custom commands.");
            return;
        }

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement entry in custom.EnumerateArray())
        {
            CustomCommandConfig? command = ParseCustomCommand(entry, index, seenKeys);
            if (command is not null)
            {
                commands.Custom.Add(command);
            }

            index++;
        }
    }

    /// <summary>Parses one <c>commands.custom</c> entry; null (after one WARN) = entry dropped.</summary>
    private CustomCommandConfig? ParseCustomCommand(JsonElement entry, int index, HashSet<string> seenKeys)
    {
        string where = $"commands.custom[{index}]";
        if (entry.ValueKind != JsonValueKind.Object)
        {
            _log("WARN", $"config.json \"{where}\" is not an object; entry dropped.");
            return null;
        }

        if (!entry.TryGetProperty("key", out JsonElement keyElement)
            || keyElement.ValueKind != JsonValueKind.String
            || keyElement.GetString() is not { } key
            || !CommandKey.IsValid(key))
        {
            _log("WARN", $"config.json \"{where}.key\" must be a kebab-case slug of at most {CommandKey.MaxLength} characters; entry dropped.");
            return null;
        }

        if (!seenKeys.Add(key))
        {
            _log("WARN", $"config.json \"{where}.key\" duplicates an earlier key; entry dropped.");
            return null;
        }

        CustomActionConfig? action = ParseCustomAction(entry, where);
        if (action is null)
        {
            return null;
        }

        var command = new CustomCommandConfig { Key = key, Name = key, Action = action };
        if (entry.TryGetProperty("name", out JsonElement name))
        {
            if (name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } value)
            {
                command.Name = value;
            }
            else
            {
                _log("WARN", $"config.json \"{where}.name\" must be a non-empty string; using the key \"{key}\".");
            }
        }

        if (entry.TryGetProperty("enabled", out JsonElement enabled))
        {
            if (enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                command.Enabled = enabled.GetBoolean();
            }
            else
            {
                _log("WARN", $"config.json \"{where}.enabled\" must be a boolean; using default {command.Enabled}.");
            }
        }

        return command;
    }

    /// <summary>Parses a custom entry's <c>action</c>; null (after one WARN) = the whole entry must be dropped.</summary>
    private CustomActionConfig? ParseCustomAction(JsonElement entry, string where)
    {
        if (!entry.TryGetProperty("action", out JsonElement action) || action.ValueKind != JsonValueKind.Object)
        {
            _log("WARN", $"config.json \"{where}.action\" must be an object; entry dropped.");
            return null;
        }

        if (!action.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String)
        {
            _log("WARN", $"config.json \"{where}.action.type\" must be a string; entry dropped.");
            return null;
        }

        switch (type.GetString())
        {
            case "mediaKey":
            {
                MediaKeyName? keyName =
                    action.TryGetProperty("keyName", out JsonElement keyNameElement) && keyNameElement.ValueKind == JsonValueKind.String
                        ? ParseMediaKeyName(keyNameElement.GetString())
                        : null;
                if (keyName is null)
                {
                    _log("WARN", $"config.json \"{where}.action.keyName\" must be one of playPause/next/previous/stop/mute/volumeUp/volumeDown; entry dropped.");
                    return null;
                }

                return new MediaKeyActionConfig { KeyName = keyName.Value };
            }

            case "launch":
            {
                if (!action.TryGetProperty("path", out JsonElement path)
                    || path.ValueKind != JsonValueKind.String
                    || path.GetString() is not { Length: > 0 } pathValue)
                {
                    _log("WARN", $"config.json \"{where}.action.path\" must be a non-empty string; entry dropped.");
                    return null;
                }

                var launch = new LaunchActionConfig { Path = pathValue };
                if (action.TryGetProperty("args", out JsonElement args))
                {
                    if (args.ValueKind == JsonValueKind.String)
                    {
                        launch.Args = args.GetString()!;
                    }
                    else
                    {
                        _log("WARN", $"config.json \"{where}.action.args\" must be a string; using no arguments.");
                    }
                }

                return launch;
            }

            default:
                _log("WARN", $"config.json \"{where}.action.type\" is not a known action type (mediaKey/launch); entry dropped.");
                return null;
        }
    }

    private static MediaKeyName? ParseMediaKeyName(string? wireName) => wireName switch
    {
        "playPause" => MediaKeyName.PlayPause,
        "next" => MediaKeyName.Next,
        "previous" => MediaKeyName.Previous,
        "stop" => MediaKeyName.Stop,
        "mute" => MediaKeyName.Mute,
        "volumeUp" => MediaKeyName.VolumeUp,
        "volumeDown" => MediaKeyName.VolumeDown,
        _ => null,
    };

    /// <summary>Reads one legacy device-name field, falling back to <paramref name="defaultValue"/> for a missing, non-string, or empty value.</summary>
    private string LegacyNameOrDefault(JsonElement deviceNames, string field, string defaultValue)
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

    private void ApplyBridgeEnabled(JsonElement root, BridgeConfig result)
    {
        if (!root.TryGetProperty("bridgeEnabled", out JsonElement element))
        {
            return;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            result.BridgeEnabled = element.GetBoolean();
            return;
        }

        _log("WARN", $"config.json \"bridgeEnabled\" must be a boolean; using default {result.BridgeEnabled}.");
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
