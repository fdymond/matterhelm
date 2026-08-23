using System.Text.Json;
using System.Text.Json.Serialization;
using MatterHelm.Actions;

namespace MatterHelm;

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

    /// <summary>Dedicated play (absolute — starts playback, never pauses; S9-1).</summary>
    Play,

    /// <summary>Dedicated pause (absolute — pauses playback, never resumes; S9-1).</summary>
    Pause,
}

/// <summary>
/// What a custom command does when its endpoint fires (ADR-004 §1, extended
/// by S7-1 and S8-3). Executed by the tray app only — the sidecar never sees
/// actions. The wire form is polymorphic on <c>type</c>
/// (<c>mediaKey</c> | <c>launch</c> | <c>keySequence</c> | <c>delay</c> |
/// <c>sequence</c>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MediaKeyActionConfig), "mediaKey")]
[JsonDerivedType(typeof(LaunchActionConfig), "launch")]
[JsonDerivedType(typeof(KeySequenceActionConfig), "keySequence")]
[JsonDerivedType(typeof(SystemActionConfig), "system")]
[JsonDerivedType(typeof(DelayActionConfig), "delay")]
[JsonDerivedType(typeof(SequenceActionConfig), "sequence")]
public abstract class CustomActionConfig;

/// <summary>The functional command a <c>system</c> custom action performs (S8-5).</summary>
public enum SystemCommandName
{
    /// <summary>Start the user's configured Windows screensaver.</summary>
    StartScreenSaver,

    /// <summary>Dismiss a running screensaver (net-zero mouse nudge).</summary>
    StopScreenSaver,

    /// <summary>Put all displays into their low-power state.</summary>
    DisplaysOff,

    /// <summary>Wake the displays.</summary>
    DisplaysOn,

    /// <summary>Suspend the machine.</summary>
    Sleep,

    /// <summary>Hibernate the machine (fails when hibernation is disabled).</summary>
    Hibernate,

    /// <summary>Lock the workstation (Win+L).</summary>
    Lock,

    /// <summary>Gracefully close the foreground program (WM_CLOSE, like the title-bar X).</summary>
    CloseForegroundProgram,

    /// <summary>Shut the machine down immediately.</summary>
    Shutdown,

    /// <summary>Restart the machine immediately.</summary>
    Restart,
}

/// <summary>Custom action running one functional system command (<c>{"type":"system","command":"startScreenSaver"}</c>, S8-5).</summary>
public sealed class SystemActionConfig : CustomActionConfig
{
    /// <summary>Which command to run.</summary>
    public required SystemCommandName Command { get; set; }
}

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
/// Custom action injecting a keyboard chord
/// (<c>{"type":"keySequence","sequence":"Ctrl+Shift+V"}</c>, S7-1). The
/// sequence grammar, key table, and canonical form are defined by
/// <see cref="KeyChord"/>; Config validates on load and persists the
/// canonical casing.
/// </summary>
public sealed class KeySequenceActionConfig : CustomActionConfig
{
    /// <summary>The chord in <see cref="KeyChord"/> grammar, canonical form (e.g. <c>Ctrl+Shift+V</c>).</summary>
    public required string Sequence { get; set; }
}

/// <summary>
/// Custom action that waits (<c>{"type":"delay","ms":300}</c>, S8-3). Meant
/// as a pacing step inside a <see cref="SequenceActionConfig"/> — the editor
/// only offers it there — but harmless standalone. A sequence containing any
/// delay runs on a background macro runner (S8-6), so waits never block the
/// IPC receive loop; <see cref="MaxMs"/> still bounds each step so a macro's
/// total runtime stays predictable.
/// </summary>
public sealed class DelayActionConfig : CustomActionConfig
{
    /// <summary>Smallest accepted wait.</summary>
    public const int MinMs = 1;

    /// <summary>Largest accepted wait per step (blocking; see class doc).</summary>
    public const int MaxMs = 5000;

    /// <summary>Milliseconds to wait (<see cref="MinMs"/>–<see cref="MaxMs"/>).</summary>
    public required int Ms { get; set; }
}

/// <summary>
/// Custom action running several actions in order — a macro
/// (<c>{"type":"sequence","steps":[{action}, …]}</c>, S8-3). Steps are the
/// other action types (media key, launch, key sequence, delay); nesting a
/// sequence inside a sequence is rejected on load and in the editor.
/// Execution stops at the first failing step. An instant sequence (no delay
/// steps) runs inline and its ack/nack names the failing step; a
/// delay-bearing sequence runs on a background macro runner (S8-6) — its ack
/// means "started" and the outcome lands in the log + overlay. The step count
/// and summed delay stay capped so a macro's total runtime is bounded.
/// </summary>
public sealed class SequenceActionConfig : CustomActionConfig
{
    /// <summary>Most steps a sequence may hold.</summary>
    public const int MaxSteps = 16;

    /// <summary>Cap on the sum of all delay steps in one sequence.</summary>
    public const int MaxTotalDelayMs = 10000;

    /// <summary>The steps, run in list order.</summary>
    public required List<CustomActionConfig> Steps { get; set; }
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

/// <summary>The overlay HUD color theme (S9-4).</summary>
public enum OverlayTheme
{
    /// <summary>Follow the Windows apps light/dark setting (the default).</summary>
    System,

    /// <summary>Always the dark panel (the classic look).</summary>
    Dark,

    /// <summary>Always the light panel.</summary>
    Light,
}

/// <summary>
/// The eight standard screen placements for the overlay HUD, relative to the
/// primary screen's working area (so the taskbar is never covered).
/// </summary>
public enum OverlayPosition
{
    /// <summary>Top-left corner.</summary>
    TopLeft,

    /// <summary>Top edge, horizontally centered.</summary>
    TopCenter,

    /// <summary>Top-right corner.</summary>
    TopRight,

    /// <summary>Left edge, vertically centered.</summary>
    MiddleLeft,

    /// <summary>Right edge, vertically centered.</summary>
    MiddleRight,

    /// <summary>Bottom-left corner.</summary>
    BottomLeft,

    /// <summary>Bottom edge, horizontally centered (the default).</summary>
    BottomCenter,

    /// <summary>Bottom-right corner.</summary>
    BottomRight,
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

    /// <summary>
    /// How long after an "on" tap a momentary Google Home switch snaps back
    /// to "off", in milliseconds (S7-1; integer 0–2000, 0 = next-tick reset
    /// per S8-2 — safe since ADR-008 dispatches on the command). Threaded to the
    /// sidecar via <c>HTPC_BRIDGE_MOMENTARY_RESET_MS</c>; the default must
    /// equal the bridge's <c>DEFAULT_MOMENTARY_RESET_MS</c> (0 = immediate
    /// since S8-2 — the reset is presentation-only per ADR-008).
    /// </summary>
    public int MomentaryResetMs { get; set; }

    /// <summary>Whether the overlay HUD flashes on commands.</summary>
    public bool OverlayEnabled { get; set; } = true;

    /// <summary>Where the overlay HUD sits on the primary screen's working area.</summary>
    public OverlayPosition OverlayPosition { get; set; } = OverlayPosition.BottomCenter;

    /// <summary>Overlay color theme (S9-4): follow the Windows apps theme (default), or force dark/light.</summary>
    public OverlayTheme OverlayTheme { get; set; } = OverlayTheme.System;

    /// <summary>Overlay panel opacity percent, 30-100 (S9-4; 100 = the classic look).</summary>
    public int OverlayOpacityPercent { get; set; } = 100;

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

    /// <summary>Minimum level of the tray app's own log (ADR-006 §2). Applied live via <c>Log.MinimumLevel</c> — no restart.</summary>
    public string AppLogLevel { get; set; } = "info";
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
/// (default <c>%APPDATA%\MatterHelm\config.json</c>, camelCase JSON,
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
    /// <c>%APPDATA%\MatterHelm\config.json</c>.
    /// </summary>
    /// <param name="path">Config file path; pass a temp-directory path in tests — never the real profile.</param>
    /// <param name="log">Log sink (level, message); defaults to <see cref="MatterHelm.Log"/>. Injectable for tests.</param>
    public Config(string? path = null, Action<string, string>? log = null)
    {
        _path = path ?? DefaultPath;
        _log = log ?? DefaultLog;
        Current = LoadOrCreate();
    }

    /// <summary>The default config path: <c>%APPDATA%\MatterHelm\config.json</c>. Computed per access from <see cref="AppPaths.Root"/> so the S7-2 migration fallback applies.</summary>
    public static string DefaultPath => Path.Combine(AppPaths.Root, "config.json");

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
            ApplyMomentaryResetMs(root, result);
            ApplyPowerOffAction(root, result);
            ApplyOverlayEnabled(root, result);
            ApplyOverlayPosition(root, result);
            ApplyOverlayTheme(root, result);
            ApplyOverlayOpacityPercent(root, result);
            ApplyBridgeEnabled(root, result);
            ApplyMdnsInterface(root, result);
            ApplyLogLevel(root, result);
            ApplyAppLogLevel(root, result);
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

        return ParseActionObject(action, $"{where}.action", allowSequence: true);
    }

    /// <summary>
    /// Parses one action object — a top-level custom action or one sequence
    /// step (S8-3; <paramref name="allowSequence"/> false inside a sequence,
    /// so macros never nest). Null (after one WARN) = drop the whole entry.
    /// </summary>
    private CustomActionConfig? ParseActionObject(JsonElement action, string where, bool allowSequence)
    {
        if (!action.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String)
        {
            _log("WARN", $"config.json \"{where}.type\" must be a string; entry dropped.");
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
                    _log("WARN", $"config.json \"{where}.keyName\" must be one of playPause/next/previous/stop/mute/volumeUp/volumeDown/play/pause; entry dropped.");
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
                    _log("WARN", $"config.json \"{where}.path\" must be a non-empty string; entry dropped.");
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
                        _log("WARN", $"config.json \"{where}.args\" must be a string; using no arguments.");
                    }
                }

                return launch;
            }

            case "keySequence":
            {
                if (!action.TryGetProperty("sequence", out JsonElement sequence)
                    || sequence.ValueKind != JsonValueKind.String
                    || sequence.GetString() is not { } raw
                    || !KeyChord.TryParse(raw, out ParsedKeyChord? chord, out _))
                {
                    _log("WARN", $"config.json \"{where}.sequence\" must be a valid key sequence like \"Ctrl+Shift+V\"; entry dropped.");
                    return null;
                }

                // Persisted form is canonical (fixed modifier order, table
                // casing) — case-insensitive input, canonical casing out.
                return new KeySequenceActionConfig { Sequence = chord.Canonical };
            }

            case "system":
            {
                SystemCommandName? command =
                    action.TryGetProperty("command", out JsonElement commandElement) && commandElement.ValueKind == JsonValueKind.String
                        ? ParseSystemCommandName(commandElement.GetString())
                        : null;
                if (command is null)
                {
                    _log("WARN", $"config.json \"{where}.command\" must be one of startScreenSaver/stopScreenSaver/displaysOff/displaysOn/sleep/hibernate/lock/closeForegroundProgram/shutdown/restart; entry dropped.");
                    return null;
                }

                return new SystemActionConfig { Command = command.Value };
            }

            case "delay":
            {
                if (!action.TryGetProperty("ms", out JsonElement ms)
                    || ms.ValueKind != JsonValueKind.Number
                    || !ms.TryGetInt32(out int msValue)
                    || msValue is < DelayActionConfig.MinMs or > DelayActionConfig.MaxMs)
                {
                    _log("WARN", $"config.json \"{where}.ms\" must be an integer {DelayActionConfig.MinMs}-{DelayActionConfig.MaxMs}; entry dropped.");
                    return null;
                }

                return new DelayActionConfig { Ms = msValue };
            }

            case "sequence":
            {
                if (!allowSequence)
                {
                    _log("WARN", $"config.json \"{where}\": a sequence cannot contain another sequence; entry dropped.");
                    return null;
                }

                if (!action.TryGetProperty("steps", out JsonElement steps)
                    || steps.ValueKind != JsonValueKind.Array
                    || steps.GetArrayLength() is < 1 or > SequenceActionConfig.MaxSteps)
                {
                    _log("WARN", $"config.json \"{where}.steps\" must be an array of 1-{SequenceActionConfig.MaxSteps} actions; entry dropped.");
                    return null;
                }

                var parsed = new List<CustomActionConfig>();
                int index = 0;
                int totalDelayMs = 0;
                foreach (JsonElement step in steps.EnumerateArray())
                {
                    if (step.ValueKind != JsonValueKind.Object)
                    {
                        _log("WARN", $"config.json \"{where}.steps[{index}]\" must be an action object; entry dropped.");
                        return null;
                    }

                    if (ParseActionObject(step, $"{where}.steps[{index}]", allowSequence: false) is not { } stepAction)
                    {
                        // The step parser already warned; a macro with a
                        // broken step must not half-run.
                        return null;
                    }

                    if (stepAction is DelayActionConfig delay)
                    {
                        totalDelayMs += delay.Ms;
                    }

                    parsed.Add(stepAction);
                    index++;
                }

                if (totalDelayMs > SequenceActionConfig.MaxTotalDelayMs)
                {
                    _log("WARN", $"config.json \"{where}.steps\" delays sum to {totalDelayMs} ms, over the {SequenceActionConfig.MaxTotalDelayMs} ms cap; entry dropped.");
                    return null;
                }

                return new SequenceActionConfig { Steps = parsed };
            }

            default:
                _log("WARN", $"config.json \"{where}.type\" is not a known action type (mediaKey/launch/keySequence/system/delay/sequence); entry dropped.");
                return null;
        }
    }

    private static SystemCommandName? ParseSystemCommandName(string? wireName) => wireName switch
    {
        "startScreenSaver" => SystemCommandName.StartScreenSaver,
        "stopScreenSaver" => SystemCommandName.StopScreenSaver,
        "displaysOff" => SystemCommandName.DisplaysOff,
        "displaysOn" => SystemCommandName.DisplaysOn,
        "sleep" => SystemCommandName.Sleep,
        "hibernate" => SystemCommandName.Hibernate,
        "lock" => SystemCommandName.Lock,
        "closeForegroundProgram" => SystemCommandName.CloseForegroundProgram,
        "shutdown" => SystemCommandName.Shutdown,
        "restart" => SystemCommandName.Restart,
        _ => null,
    };

    private static MediaKeyName? ParseMediaKeyName(string? wireName) => wireName switch
    {
        "playPause" => MediaKeyName.PlayPause,
        "next" => MediaKeyName.Next,
        "previous" => MediaKeyName.Previous,
        "stop" => MediaKeyName.Stop,
        "mute" => MediaKeyName.Mute,
        "volumeUp" => MediaKeyName.VolumeUp,
        "volumeDown" => MediaKeyName.VolumeDown,
        "play" => MediaKeyName.Play,
        "pause" => MediaKeyName.Pause,
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

    private void ApplyMomentaryResetMs(JsonElement root, BridgeConfig result)
    {
        if (!root.TryGetProperty("momentaryResetMs", out JsonElement element))
        {
            return;
        }

        // The bridge's env parser is strict and treats an out-of-range value
        // as FATAL — same discipline as logLevel (S4-R RISK-2): never hand
        // the sidecar a value that would crash-loop it.
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int ms) && ms is >= 0 and <= 2000)
        {
            result.MomentaryResetMs = ms;
            return;
        }

        _log("WARN", $"config.json \"momentaryResetMs\" must be an integer 0-2000; using default {result.MomentaryResetMs}.");
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

    private void ApplyOverlayPosition(JsonElement root, BridgeConfig result)
    {
        if (!root.TryGetProperty("overlayPosition", out JsonElement element))
        {
            return;
        }

        // Digit guard: Enum.TryParse accepts numeric strings ("5" would parse
        // as the underlying value) — only member names are valid wire forms.
        if (element.ValueKind == JsonValueKind.String
            && element.GetString() is { Length: > 0 } raw
            && !char.IsAsciiDigit(raw[0])
            && Enum.TryParse(raw, ignoreCase: true, out OverlayPosition position)
            && Enum.IsDefined(position))
        {
            result.OverlayPosition = position;
            return;
        }

        _log("WARN", "config.json \"overlayPosition\" must be one of topLeft/topCenter/topRight/middleLeft/middleRight/bottomLeft/bottomCenter/bottomRight; using default bottomCenter.");
    }

    private void ApplyOverlayTheme(JsonElement root, BridgeConfig result)
    {
        if (!root.TryGetProperty("overlayTheme", out JsonElement element))
        {
            return;
        }

        // Same digit guard as overlayPosition: member names only.
        if (element.ValueKind == JsonValueKind.String
            && element.GetString() is { Length: > 0 } raw
            && !char.IsAsciiDigit(raw[0])
            && Enum.TryParse(raw, ignoreCase: true, out OverlayTheme theme)
            && Enum.IsDefined(theme))
        {
            result.OverlayTheme = theme;
            return;
        }

        _log("WARN", "config.json \"overlayTheme\" must be one of system/dark/light; using default system.");
    }

    private void ApplyOverlayOpacityPercent(JsonElement root, BridgeConfig result)
    {
        if (!root.TryGetProperty("overlayOpacityPercent", out JsonElement element))
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int percent) && percent is >= 30 and <= 100)
        {
            result.OverlayOpacityPercent = percent;
            return;
        }

        _log("WARN", $"config.json \"overlayOpacityPercent\" must be an integer 30-100; using default {result.OverlayOpacityPercent}.");
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
            // S4-R RISK-2: the bridge's config parser is strict and treats an
            // unknown level as FATAL — an unvalidated value here would load
            // fine in the tray app and then crash-loop the sidecar (red icon,
            // no visible reason). Same fallback discipline as every field.
            if (KnownLogLevels.Contains(value))
            {
                result.LogLevel = value;
                return;
            }

            _log("WARN", $"config.json \"logLevel\" \"{value}\" is not a pino level ({string.Join("/", KnownLogLevels)}); using default \"{result.LogLevel}\".");
            return;
        }

        _log("WARN", $"config.json \"logLevel\" must be a non-empty string; using default \"{result.LogLevel}\".");
    }

    private void ApplyAppLogLevel(JsonElement root, BridgeConfig result)
    {
        if (!root.TryGetProperty("appLogLevel", out JsonElement element))
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } value)
        {
            if (KnownAppLogLevels.Contains(value))
            {
                result.AppLogLevel = value;
                return;
            }

            _log("WARN", $"config.json \"appLogLevel\" \"{value}\" is not one of {string.Join("/", KnownAppLogLevels)}; using default \"{result.AppLogLevel}\".");
            return;
        }

        _log("WARN", $"config.json \"appLogLevel\" must be a non-empty string; using default \"{result.AppLogLevel}\".");
    }

    /// <summary>The pino levels the bridge sidecar accepts (its parser is strict — bridge/src/config.ts).</summary>
    private static readonly string[] KnownLogLevels = ["trace", "debug", "info", "warn", "error", "fatal", "silent"];

    /// <summary>The tray app's own log levels (ADR-006 §2; must stay parseable by <c>Log.ParseLevel</c>).</summary>
    private static readonly string[] KnownAppLogLevels = ["debug", "info", "warn", "error"];

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
