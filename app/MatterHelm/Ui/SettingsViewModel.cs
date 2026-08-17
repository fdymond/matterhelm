using System.Text.Json;
using MatterHelm.Actions;

namespace MatterHelm.Ui;

/// <summary>How a setting is edited, which tells <see cref="SettingsWindow"/> what control to build for it.</summary>
public enum SettingKind
{
    /// <summary>Boolean checkbox.</summary>
    Toggle,

    /// <summary>TCP port number (1–65535).</summary>
    Port,

    /// <summary>Integer within the descriptor's <see cref="SettingDescriptor.Minimum"/>–<see cref="SettingDescriptor.Maximum"/> range.</summary>
    Number,

    /// <summary>Required free text (must be non-empty to save).</summary>
    Text,

    /// <summary>Optional free text (empty means "not set"/auto).</summary>
    OptionalText,

    /// <summary>One value from <see cref="SettingDescriptor.Choices"/>.</summary>
    Choice,

    /// <summary>The custom-command list editor (ListView + add/edit/remove).</summary>
    CustomCommands,

    /// <summary>Read-only informational text.</summary>
    ReadOnlyText,

    /// <summary>A button that performs an action rather than editing a value.</summary>
    Command,
}

/// <summary>
/// One row of the settings window, described declaratively (ADR-004 §4): the
/// window builds its editor control from <see cref="Kind"/>, and
/// <see cref="SettingsSearch"/> filters on <see cref="Label"/>/
/// <see cref="Description"/>. <see cref="Get"/>/<see cref="Set"/> read/write
/// the staged working copy, never the live config.
/// </summary>
public sealed class SettingDescriptor
{
    /// <summary>Stable id (kebab-case) used for filtering, validation targeting, and demo hooks.</summary>
    public required string Id { get; init; }

    /// <summary>Short user-facing label.</summary>
    public required string Label { get; init; }

    /// <summary>One-sentence user-facing description (searched, shown in gray under the label).</summary>
    public required string Description { get; init; }

    /// <summary>Which editor control the window builds.</summary>
    public required SettingKind Kind { get; init; }

    /// <summary>Wire values for <see cref="SettingKind.Choice"/>; empty otherwise.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>Display labels parallel to <see cref="Choices"/>; empty = show the wire values.</summary>
    public IReadOnlyList<string> ChoiceLabels { get; init; } = [];

    /// <summary>Lower bound for <see cref="SettingKind.Number"/>; unused otherwise.</summary>
    public int Minimum { get; init; }

    /// <summary>Upper bound for <see cref="SettingKind.Number"/>; unused otherwise.</summary>
    public int Maximum { get; init; } = int.MaxValue;

    /// <summary>True = the row shows "takes effect next time the bridge is enabled" (ADR-004 §4).</summary>
    public bool NeedsBridgeRestart { get; init; }

    /// <summary>Reads the setting's current value from a working copy; null for kinds without a value.</summary>
    public Func<BridgeConfig, object?>? Get { get; init; }

    /// <summary>Writes an edited value into a working copy; null for kinds without a value.</summary>
    public Action<BridgeConfig, object?>? Set { get; init; }
}

/// <summary>One left-nav category of the settings window with its ordered setting rows.</summary>
public sealed class SettingsCategory
{
    /// <summary>Stable id (kebab-case).</summary>
    public required string Id { get; init; }

    /// <summary>Nav/display title (also matched by search).</summary>
    public required string Title { get; init; }

    /// <summary>The category's rows, in display order.</summary>
    public required IReadOnlyList<SettingDescriptor> Settings { get; init; }
}

/// <summary>One validation failure, addressed to the setting row it belongs to.</summary>
public sealed class SettingsValidationError
{
    /// <summary>Creates one error for the given descriptor id.</summary>
    public SettingsValidationError(string settingId, string message)
    {
        SettingId = settingId;
        Message = message;
    }

    /// <summary>The <see cref="SettingDescriptor.Id"/> the error belongs to.</summary>
    public string SettingId { get; }

    /// <summary>User-facing message.</summary>
    public string Message { get; }
}

/// <summary>
/// The settings window's logic, free of WinForms types so it is fully unit
/// testable (ADR-004 §4 / ADR-005 §3): a staged deep copy of the live
/// <see cref="BridgeConfig"/> (<see cref="Working"/>), dirty tracking against
/// a snapshot, validation (port range, non-empty names, custom-key slug and
/// uniqueness rules, launch-path existence), and the Save path — Apply copies
/// the working values into <see cref="MatterHelm.Config.Current"/>,
/// persists via <see cref="MatterHelm.Config.Save"/>, then
/// <see cref="MatterHelm.Config.Reload"/>s so the existing
/// <c>Config.Changed</c> machinery (tray checkboxes, overlay toggle, bridge
/// enable) applies the change live.
/// </summary>
public sealed class SettingsViewModel
{
    /// <summary>The built-in endpoint role names a custom key must never collide with (they are wire identifiers too, ADR-004 §1).</summary>
    private static readonly string[] BuiltinRoleNames = ["speaker", "playPause", "next", "previous", "power"];

    private readonly Config _config;
    private readonly Func<string, bool> _pathExists;
    private string _baseline;
    private BridgeConfig _baselineConfig;

    /// <summary>Creates a view-model staged over <paramref name="config"/>'s current state.</summary>
    /// <param name="config">The live config to stage edits against.</param>
    /// <param name="pathExists">Launch-path existence probe; defaults to <see cref="File.Exists(string?)"/>. Injectable for tests.</param>
    public SettingsViewModel(Config config, Func<string, bool>? pathExists = null)
    {
        _config = config;
        _pathExists = pathExists ?? File.Exists;
        Working = Clone(config.Current);
        _baselineConfig = Clone(config.Current);
        _baseline = Snapshot(Working);
    }

    /// <summary>The category/descriptor definitions — static because they describe the schema, not an instance's values.</summary>
    public static IReadOnlyList<SettingsCategory> Categories { get; } = BuildCategories();

    /// <summary>The user's friendly labels for the media keys a custom command can inject, in <see cref="MediaKeyName"/> declaration order.</summary>
    public static IReadOnlyList<(MediaKeyName Key, string Label)> MediaKeyChoices { get; } =
    [
        (MediaKeyName.PlayPause, "Play/pause"),
        (MediaKeyName.Next, "Next track"),
        (MediaKeyName.Previous, "Previous track"),
        (MediaKeyName.Stop, "Stop"),
        (MediaKeyName.Mute, "Mute"),
        (MediaKeyName.VolumeUp, "Volume up"),
        (MediaKeyName.VolumeDown, "Volume down"),
    ];

    /// <summary>Friendly labels for the S8-5 system commands, in <see cref="SystemCommandName"/> declaration order.</summary>
    public static IReadOnlyList<(SystemCommandName Command, string Label)> SystemCommandChoices { get; } =
    [
        (SystemCommandName.StartScreenSaver, "Start screensaver"),
        (SystemCommandName.StopScreenSaver, "Stop screensaver"),
        (SystemCommandName.DisplaysOff, "Displays off"),
        (SystemCommandName.DisplaysOn, "Displays on"),
        (SystemCommandName.Sleep, "Sleep"),
        (SystemCommandName.Hibernate, "Hibernate"),
        (SystemCommandName.Lock, "Lock the PC"),
        (SystemCommandName.CloseForegroundProgram, "Close focused program"),
        (SystemCommandName.Shutdown, "Shut down"),
        (SystemCommandName.Restart, "Restart"),
    ];

    /// <summary>Where the bridge keeps Matter fabric state (BLUEPRINT §2.5) — display-only on the Advanced page. Derived from <see cref="AppPaths.Root"/> so the S7-2 migration fallback shows the directory actually in use.</summary>
    public static string StorageDirDisplay => Path.Combine(AppPaths.Root, "matter");

    /// <summary>The staged working copy all editors read and write. Replaced (not mutated) by <see cref="Apply"/>/<see cref="Revert"/>.</summary>
    public BridgeConfig Working { get; private set; }

    /// <summary>True iff the working copy differs from the last applied/loaded state.</summary>
    public bool IsDirty => Snapshot(Working) != _baseline;

    /// <summary>True iff <see cref="Validate"/> finds nothing wrong.</summary>
    public bool IsValid => Validate().Count == 0;

    /// <summary>Finds the descriptor with the given id across all categories (throws for an unknown id — ids are compile-time constants).</summary>
    public static SettingDescriptor Describe(string settingId) =>
        Categories.SelectMany(c => c.Settings).First(s => s.Id == settingId);

    /// <summary>Validates the working copy; empty = saveable.</summary>
    public IReadOnlyList<SettingsValidationError> Validate()
    {
        List<SettingsValidationError> errors = [];
        if (Working.IpcPort is < 1 or > 65535)
        {
            errors.Add(new SettingsValidationError("ipc-port", "Port must be between 1 and 65535."));
        }

        if (Working.MomentaryResetMs is < 0 or > 2000)
        {
            errors.Add(new SettingsValidationError("momentary-reset-ms", "Reset delay must be between 0 and 2000 ms."));
        }

        ValidateBuiltinName(errors, "speaker-name", Working.Commands.Speaker.Name);
        ValidateBuiltinName(errors, "play-pause-name", Working.Commands.PlayPause.Name);
        ValidateBuiltinName(errors, "next-name", Working.Commands.Next.Name);
        ValidateBuiltinName(errors, "previous-name", Working.Commands.Previous.Name);
        ValidateBuiltinName(errors, "power-name", Working.Commands.Power.Name);

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (CustomCommandConfig command in Working.Commands.Custom)
        {
            string? keyError = !seenKeys.Add(command.Key)
                ? "Another custom command already uses this key."
                : ValidateCustomCommandKey(command.Key, originalKey: command.Key);
            if (keyError is not null)
            {
                errors.Add(new SettingsValidationError("custom-commands", $"\"{command.Key}\": {keyError}"));
            }

            if (ValidateCustomCommandName(command.Name) is { } nameError)
            {
                errors.Add(new SettingsValidationError("custom-commands", $"\"{command.Key}\": {nameError}"));
            }

            if (ValidateAction(command.Action, allowSequence: true) is { } actionError)
            {
                errors.Add(new SettingsValidationError("custom-commands", $"\"{command.Key}\": {actionError}"));
            }
        }

        return errors;
    }

    /// <summary>
    /// Validates one custom action — a command's own action or one sequence
    /// step (S8-3; <paramref name="allowSequence"/> false for steps, so macros
    /// never nest). Null = valid. Sequence errors carry the 1-based step
    /// number; the caps mirror Config's load-time parser exactly.
    /// </summary>
    public string? ValidateAction(CustomActionConfig action, bool allowSequence) => action switch
    {
        LaunchActionConfig launch => ValidateLaunchPath(launch.Path),
        KeySequenceActionConfig keySequence => ValidateKeySequence(keySequence.Sequence),
        DelayActionConfig delay when delay.Ms is < DelayActionConfig.MinMs or > DelayActionConfig.MaxMs =>
            $"Delay must be {DelayActionConfig.MinMs}–{DelayActionConfig.MaxMs} ms.",
        SequenceActionConfig when !allowSequence => "A sequence cannot contain another sequence.",
        SequenceActionConfig sequence => ValidateSequence(sequence),
        _ => null,
    };

    private string? ValidateSequence(SequenceActionConfig sequence)
    {
        if (sequence.Steps.Count is < 1 or > SequenceActionConfig.MaxSteps)
        {
            return $"A sequence needs 1–{SequenceActionConfig.MaxSteps} steps.";
        }

        for (int i = 0; i < sequence.Steps.Count; i++)
        {
            if (ValidateAction(sequence.Steps[i], allowSequence: false) is { } stepError)
            {
                return $"Step {i + 1}: {stepError}";
            }
        }

        int totalDelayMs = sequence.Steps.OfType<DelayActionConfig>().Sum(d => d.Ms);
        return totalDelayMs > SequenceActionConfig.MaxTotalDelayMs
            ? $"Delays sum to {totalDelayMs} ms — the cap is {SequenceActionConfig.MaxTotalDelayMs} ms."
            : null;
    }

    /// <summary>
    /// Validates a (proposed) custom-command key: slug shape via
    /// <see cref="CommandKey"/>, no collision with built-in role names, and
    /// uniqueness among the working custom commands.
    /// <paramref name="originalKey"/> excludes the command being edited from
    /// its own uniqueness check (the key is locked while editing, but the
    /// aggregate <see cref="Validate"/> reuses this too). Null = valid.
    /// </summary>
    public string? ValidateCustomCommandKey(string key, string? originalKey = null)
    {
        if (!CommandKey.IsValid(key))
        {
            return $"Key must be a kebab-case slug (a–z, 0–9, single hyphens) of at most {CommandKey.MaxLength} characters.";
        }

        if (BuiltinRoleNames.Contains(key, StringComparer.Ordinal))
        {
            return $"\"{key}\" is reserved for a built-in command.";
        }

        if (Working.Commands.Custom.Any(c => c.Key == key && c.Key != originalKey))
        {
            return "Another custom command already uses this key.";
        }

        return null;
    }

    /// <summary>Validates a custom command's display name (non-empty). Null = valid.</summary>
    public static string? ValidateCustomCommandName(string name) =>
        string.IsNullOrWhiteSpace(name) ? "Name must not be empty." : null;

    /// <summary>Validates a launch action's program path (must exist at save time, ADR-004 §1). Null = valid.</summary>
    public string? ValidateLaunchPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Program path must not be empty.";
        }

        return _pathExists(path) ? null : "Program path does not exist.";
    }

    /// <summary>Validates a keySequence action's chord via the <see cref="KeyChord"/> grammar (S7-1). Null = valid; otherwise the parser's user-facing error.</summary>
    public static string? ValidateKeySequence(string sequence) =>
        KeyChord.TryParse(sequence, out _, out string? error) ? null : error;

    /// <summary>Short human summary of a custom action for the command/step lists ("Media key: stop" / "Launch: kodi.exe" / "Key sequence: Ctrl+Shift+V" / "Wait: 300 ms" / "Sequence: 3 steps").</summary>
    public static string DescribeAction(CustomActionConfig action) => action switch
    {
        MediaKeyActionConfig mediaKey => $"Media key: {MediaKeyChoices.First(c => c.Key == mediaKey.KeyName).Label.ToLowerInvariant()}",
        LaunchActionConfig launch => $"Launch: {Path.GetFileName(launch.Path)}",
        KeySequenceActionConfig keySequence => $"Key sequence: {keySequence.Sequence}",
        SystemActionConfig system =>
            $"System: {SystemCommandChoices.First(c => c.Command == system.Command).Label.ToLowerInvariant()}",
        DelayActionConfig delay => $"Wait: {delay.Ms} ms",
        SequenceActionConfig sequence => $"Sequence: {sequence.Steps.Count} step{(sequence.Steps.Count == 1 ? "" : "s")}",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action.GetType().Name, "unknown custom action type"),
    };

    /// <summary>Adds a custom command to the working copy (validated on save; the dialog pre-validates).</summary>
    public void AddCustomCommand(CustomCommandConfig command) => Working.Commands.Custom.Add(command);

    /// <summary>Replaces the working custom command with <paramref name="key"/> in place (preserving list order).</summary>
    public void UpdateCustomCommand(string key, CustomCommandConfig replacement)
    {
        int index = Working.Commands.Custom.FindIndex(c => c.Key == key);
        if (index < 0)
        {
            throw new InvalidOperationException($"no custom command with key \"{key}\"");
        }

        Working.Commands.Custom[index] = replacement;
    }

    /// <summary>Removes the working custom command with <paramref name="key"/> (no-op if absent).</summary>
    public void RemoveCustomCommand(string key) => Working.Commands.Custom.RemoveAll(c => c.Key == key);

    /// <summary>
    /// Saves the staged edits: copies <see cref="Working"/> into the live
    /// config, persists, then reloads so <c>Config.Changed</c> fires and the
    /// existing subscribers (tray checkboxes → bridge/overlay) apply what can
    /// apply live. Throws if <see cref="Validate"/> fails — the window keeps
    /// Save disabled while invalid.
    /// </summary>
    public void Apply()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException("cannot apply an invalid working copy");
        }

        CopyInto(Working, _config.Current);
        _config.Save();
        _config.Reload();
        Revert();
    }

    /// <summary>The live config this view-model stages over (for <c>Config.Changed</c> subscriptions).</summary>
    public Config Config => _config;

    /// <summary>Discards the staged edits, re-cloning from the live config.</summary>
    public void Revert()
    {
        Working = Clone(_config.Current);
        _baselineConfig = Clone(_config.Current);
        _baseline = Snapshot(Working);
    }

    /// <summary>
    /// Reconciles an external live-config change (tray toggles, tray "Reload
    /// config") into the staged state — S4-R RISK-1: without this, Save on a
    /// window opened before the change writes the stale copy back, silently
    /// reverting the tray action. Not dirty → plain re-stage. Dirty → staged
    /// edits are preserved, but any field the user has NOT diverged from the
    /// old baseline is synced to the new live value (so an untouched "Enable
    /// bridge" can't be un-toggled by Save), and the baseline moves to the new
    /// live truth so dirtiness is judged against it.
    /// </summary>
    public void AbsorbExternalConfigChange()
    {
        if (!IsDirty)
        {
            Revert();
            return;
        }

        BridgeConfig live = _config.Current;
        if (Working.BridgeEnabled == _baselineConfig.BridgeEnabled)
        {
            Working.BridgeEnabled = live.BridgeEnabled;
        }

        if (Working.OverlayEnabled == _baselineConfig.OverlayEnabled)
        {
            Working.OverlayEnabled = live.OverlayEnabled;
        }

        _baselineConfig = Clone(live);
        _baseline = Snapshot(_baselineConfig);
    }

    /// <summary>Advanced-page "Reload config": re-reads the file (firing <c>Config.Changed</c>) and re-stages from the result, discarding staged edits.</summary>
    public void ReloadFromDisk()
    {
        _config.Reload();
        Revert();
    }

    private static void ValidateBuiltinName(List<SettingsValidationError> errors, string settingId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add(new SettingsValidationError(settingId, "Name must not be empty."));
        }
    }

    /// <summary>Deep copy via a JSON round-trip — the config DTOs are exactly the JSON document, so this is lossless (incl. the polymorphic actions).</summary>
    private static BridgeConfig Clone(BridgeConfig source) =>
        JsonSerializer.Deserialize(Snapshot(source), ConfigJsonContext.Default.BridgeConfig)!;

    /// <summary>Canonical serialized form used for dirty comparison (property order is fixed by the DTO declarations, so equality is well-defined).</summary>
    private static string Snapshot(BridgeConfig config) =>
        JsonSerializer.Serialize(config, ConfigJsonContext.Default.BridgeConfig);

    private static void CopyInto(BridgeConfig from, BridgeConfig into)
    {
        into.Commands = Clone(from).Commands;
        into.IpcPort = from.IpcPort;
        into.MomentaryResetMs = from.MomentaryResetMs;
        into.PowerOffAction = from.PowerOffAction;
        into.OverlayEnabled = from.OverlayEnabled;
        into.OverlayPosition = from.OverlayPosition;
        into.BridgeEnabled = from.BridgeEnabled;
        into.MdnsInterface = from.MdnsInterface;
        into.LogLevel = from.LogLevel;
        into.AppLogLevel = from.AppLogLevel;
    }

    private static IReadOnlyList<SettingsCategory> BuildCategories() =>
    [
        new SettingsCategory
        {
            Id = "general",
            Title = "General",
            Settings =
            [
                new SettingDescriptor
                {
                    Id = "bridge-enabled",
                    Label = "Enable bridge",
                    Description = "Run the Matter bridge (sidecar and local IPC server). Same switch as the tray menu.",
                    Kind = SettingKind.Toggle,
                    Get = c => c.BridgeEnabled,
                    Set = (c, v) => c.BridgeEnabled = (bool)v!,
                },
                new SettingDescriptor
                {
                    Id = "ipc-port",
                    Label = "IPC port",
                    Description = "Loopback TCP port the tray app's IPC server listens on.",
                    Kind = SettingKind.Port,
                    NeedsBridgeRestart = true,
                    Get = c => c.IpcPort,
                    Set = (c, v) => c.IpcPort = (int)v!,
                },
                new SettingDescriptor
                {
                    Id = "log-level",
                    Label = "Log level",
                    Description = "How much detail the bridge sidecar logs.",
                    Kind = SettingKind.Choice,
                    Choices = ["silent", "fatal", "error", "warn", "info", "debug", "trace"],
                    NeedsBridgeRestart = true,
                    Get = c => c.LogLevel,
                    Set = (c, v) => c.LogLevel = (string)v!,
                },
                new SettingDescriptor
                {
                    // ADR-006 §2: applies live (no NeedsBridgeRestart note) —
                    // Program re-applies Log.MinimumLevel on Config.Changed.
                    Id = "app-log-level",
                    Label = "App log level",
                    Description = "How much detail this app writes to its own log. Applies immediately.",
                    Kind = SettingKind.Choice,
                    Choices = ["debug", "info", "warn", "error"],
                    Get = c => c.AppLogLevel,
                    Set = (c, v) => c.AppLogLevel = (string)v!,
                },
            ],
        },
        new SettingsCategory
        {
            Id = "commands",
            Title = "Devices & Commands",
            Settings =
            [
                BuiltinName("speaker-name", "Speaker name", "Google Home device name for volume and mute.", c => c.Commands.Speaker),
                BuiltinEnabled("speaker-enabled", "Speaker enabled", "Publish the speaker device to Google Home.", c => c.Commands.Speaker),
                BuiltinName("play-pause-name", "Play/pause name", "Google Home device name for the play/pause command.", c => c.Commands.PlayPause),
                BuiltinEnabled("play-pause-enabled", "Play/pause enabled", "Publish the play/pause device to Google Home.", c => c.Commands.PlayPause),
                BuiltinName("next-name", "Next name", "Google Home device name for the next-track command.", c => c.Commands.Next),
                BuiltinEnabled("next-enabled", "Next enabled", "Publish the next-track device to Google Home.", c => c.Commands.Next),
                BuiltinName("previous-name", "Previous name", "Google Home device name for the previous-track command.", c => c.Commands.Previous),
                BuiltinEnabled("previous-enabled", "Previous enabled", "Publish the previous-track device to Google Home.", c => c.Commands.Previous),
                BuiltinName("power-name", "Power name", "Google Home device name for the power switch.", c => c.Commands.Power),
                BuiltinEnabled("power-enabled", "Power enabled", "Publish the power device to Google Home.", c => c.Commands.Power),
                new SettingDescriptor
                {
                    Id = "power-off-action",
                    Label = "Power off behavior",
                    Description = "What turning the power device off does on this PC.",
                    Kind = SettingKind.Choice,
                    Choices = ["displaysOff", "pauseAndDisplaysOff", "sleep"],
                    ChoiceLabels = ["Displays off", "Pause, then displays off", "Sleep"],
                    Get = c => ToWireName(c.PowerOffAction),
                    Set = (c, v) => c.PowerOffAction = FromWireName((string)v!),
                },
                new SettingDescriptor
                {
                    // S7-1: config-driven momentary auto-reset (default 0 =
                    // immediate per S8-2, safe since ADR-008 dispatches on
                    // the command rather than the state change; range shared
                    // with the bridge's env validation).
                    Id = "momentary-reset-ms",
                    Label = "Tap reset delay (ms)",
                    Description = "How quickly a tapped command's switch snaps back to off in Google Home. 0 = immediately.",
                    Kind = SettingKind.Number,
                    Minimum = 0,
                    Maximum = 2000,
                    NeedsBridgeRestart = true,
                    Get = c => c.MomentaryResetMs,
                    Set = (c, v) => c.MomentaryResetMs = (int)v!,
                },
                new SettingDescriptor
                {
                    Id = "custom-commands",
                    Label = "Custom commands",
                    Description = "Your own commands, each an extra Google Home device. The key is the device's stable identity.",
                    Kind = SettingKind.CustomCommands,
                    NeedsBridgeRestart = true,
                },
            ],
        },
        new SettingsCategory
        {
            Id = "overlay",
            Title = "Overlay",
            Settings =
            [
                new SettingDescriptor
                {
                    Id = "overlay-enabled",
                    Label = "Overlay pop-ups",
                    Description = "Flash a brief on-screen overlay when a command arrives.",
                    Kind = SettingKind.Toggle,
                    Get = c => c.OverlayEnabled,
                    Set = (c, v) => c.OverlayEnabled = (bool)v!,
                },
                new SettingDescriptor
                {
                    Id = "overlay-position",
                    Label = "Overlay position",
                    Description = "Where the overlay appears on the screen.",
                    Kind = SettingKind.Choice,
                    Choices =
                    [
                        "topLeft", "topCenter", "topRight",
                        "middleLeft", "middleRight",
                        "bottomLeft", "bottomCenter", "bottomRight",
                    ],
                    ChoiceLabels =
                    [
                        "Top left", "Top center", "Top right",
                        "Middle left", "Middle right",
                        "Bottom left", "Bottom center", "Bottom right",
                    ],
                    Get = c => OverlayPositionToWire(c.OverlayPosition),
                    Set = (c, v) => c.OverlayPosition = OverlayPositionFromWire((string)v!),
                },
                new SettingDescriptor
                {
                    Id = "overlay-preview",
                    Label = "Preview",
                    Description = "Show a sample overlay pop-up now.",
                    Kind = SettingKind.Command,
                },
            ],
        },
        new SettingsCategory
        {
            Id = "advanced",
            Title = "Advanced",
            Settings =
            [
                new SettingDescriptor
                {
                    Id = "mdns-interface",
                    Label = "mDNS network interface",
                    Description = "Pin Matter announcements to one network interface on multi-NIC machines. Empty = auto-detect.",
                    Kind = SettingKind.OptionalText,
                    NeedsBridgeRestart = true,
                    Get = c => c.MdnsInterface ?? "",
                    Set = (c, v) => c.MdnsInterface = string.IsNullOrWhiteSpace((string?)v) ? null : (string)v!,
                },
                new SettingDescriptor
                {
                    Id = "storage-dir",
                    Label = "Matter storage",
                    Description = "Where the bridge keeps its pairing (fabric) state.",
                    Kind = SettingKind.ReadOnlyText,
                    Get = _ => StorageDirDisplay,
                },
                new SettingDescriptor
                {
                    Id = "open-config-file",
                    Label = "Config file",
                    Description = "Open config.json in your default editor.",
                    Kind = SettingKind.Command,
                },
                new SettingDescriptor
                {
                    Id = "open-config-folder",
                    Label = "Config folder",
                    Description = "Open the folder holding config.json and logs.",
                    Kind = SettingKind.Command,
                },
                new SettingDescriptor
                {
                    Id = "export-diagnostics",
                    Label = "Export diagnostics",
                    Description = "Save logs, metrics, a system manifest, and your config as a zip for troubleshooting. Nothing uploads.",
                    Kind = SettingKind.Command,
                },
                new SettingDescriptor
                {
                    Id = "reload-config",
                    Label = "Reload config",
                    Description = "Re-read config.json from disk, discarding unsaved changes here.",
                    Kind = SettingKind.Command,
                },
                new SettingDescriptor
                {
                    Id = "factory-reset",
                    Label = "Factory reset",
                    Description = "Unpair from Google Home and wipe Matter storage.",
                    Kind = SettingKind.Command,
                },
            ],
        },
    ];

    private static SettingDescriptor BuiltinName(
        string id, string label, string description, Func<BridgeConfig, BuiltinCommandConfig> builtin) => new()
    {
        Id = id,
        Label = label,
        Description = description,
        Kind = SettingKind.Text,
        NeedsBridgeRestart = true,
        Get = c => builtin(c).Name,
        Set = (c, v) => builtin(c).Name = (string)v!,
    };

    private static SettingDescriptor BuiltinEnabled(
        string id, string label, string description, Func<BridgeConfig, BuiltinCommandConfig> builtin) => new()
    {
        Id = id,
        Label = label,
        Description = description,
        Kind = SettingKind.Toggle,
        NeedsBridgeRestart = true,
        Get = c => builtin(c).Enabled,
        Set = (c, v) => builtin(c).Enabled = (bool)v!,
    };

    private static string ToWireName(PowerOffAction action) => action switch
    {
        PowerOffAction.DisplaysOff => "displaysOff",
        PowerOffAction.PauseAndDisplaysOff => "pauseAndDisplaysOff",
        PowerOffAction.Sleep => "sleep",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    private static PowerOffAction FromWireName(string wireName) => wireName switch
    {
        "displaysOff" => PowerOffAction.DisplaysOff,
        "pauseAndDisplaysOff" => PowerOffAction.PauseAndDisplaysOff,
        "sleep" => PowerOffAction.Sleep,
        _ => throw new ArgumentOutOfRangeException(nameof(wireName), wireName, null),
    };

    // camelCase of the enum member name — the exact wire form
    // OverlayPositionJsonConverter writes and Config's parser accepts.
    private static string OverlayPositionToWire(OverlayPosition position)
    {
        string name = position.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static OverlayPosition OverlayPositionFromWire(string wireName) =>
        Enum.Parse<OverlayPosition>(wireName, ignoreCase: true);
}
