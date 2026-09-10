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

    /// <summary>Integer within the range, edited with a slider + live value label (S9-7).</summary>
    Slider,

    /// <summary>Required free text (must be non-empty to save).</summary>
    Text,

    /// <summary>Optional free text (empty means "not set"/auto).</summary>
    OptionalText,

    /// <summary>One value from <see cref="SettingDescriptor.Choices"/>.</summary>
    Choice,

    /// <summary>A sensible active Windows network adapter, plus Auto, a Show-all escape hatch, and any saved adapter that is hidden or no longer detected.</summary>
    NetworkAdapterChoice,

    /// <summary>The custom-command list editor (ListView + add/edit/remove).</summary>
    CustomCommands,

    /// <summary>
    /// One built-in command as a single compact row (S9-2): a leading enabled
    /// checkbox (unticked greys the row), the label/description stack, and
    /// the device-name text editor. <see cref="SettingDescriptor.Get"/>/<c>Set</c>
    /// carry the name; <see cref="SettingDescriptor.GetEnabled"/>/<c>SetEnabled</c>
    /// carry the published flag.
    /// </summary>
    CommandRow,

    /// <summary>Bold section title with a gray description line; no editor (S9-3).</summary>
    SectionHeader,

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

    /// <summary>Reads the enabled flag for <see cref="SettingKind.CommandRow"/>; null for other kinds.</summary>
    public Func<BridgeConfig, bool>? GetEnabled { get; init; }

    /// <summary>Writes the enabled flag for <see cref="SettingKind.CommandRow"/>; null for other kinds.</summary>
    public Action<BridgeConfig, bool>? SetEnabled { get; init; }
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
/// persists the working snapshot via <see cref="MatterHelm.Config.Save()"/>, then
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
    private readonly IReadOnlyList<NetworkAdapterInfo> _networkAdapters;
    private readonly Lazy<DisplayPowerCapability> _displayPowerCapability;
    private string _baseline;
    private BridgeConfig _baselineConfig;

    /// <summary>Creates a view-model staged over <paramref name="config"/>'s current state.</summary>
    /// <param name="config">The live config to stage edits against.</param>
    /// <param name="pathExists">Launch-path existence probe; defaults to <see cref="File.Exists(string?)"/>. Injectable for tests.</param>
    public SettingsViewModel(Config config, Func<string, bool>? pathExists = null)
        : this(
            config,
            pathExists,
            SystemNetworkAdapterProvider.Instance,
            DisplayPowerCapabilityProbe.Instance)
    {
    }

    internal SettingsViewModel(
        Config config,
        Func<string, bool>? pathExists,
        INetworkAdapterProvider networkAdapterProvider,
        IDisplayPowerCapabilityProbe? displayPowerCapabilityProbe = null)
    {
        _config = config;
        _pathExists = pathExists ?? File.Exists;
        _networkAdapters = networkAdapterProvider.GetAdapters();
        IDisplayPowerCapabilityProbe probe = displayPowerCapabilityProbe ?? DisplayPowerCapabilityProbe.Instance;
        _displayPowerCapability = new Lazy<DisplayPowerCapability>(probe.Probe);
        Working = Clone(config.Current);
        _baselineConfig = Clone(config.Current);
        _baseline = Snapshot(Working);
    }

    /// <summary>The category/descriptor definitions — static because they describe the schema, not an instance's values.</summary>
    public static IReadOnlyList<SettingsCategory> Categories { get; } = SettingsCatalog.Categories;

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
        (MediaKeyName.Play, "Play (dedicated)"),
        (MediaKeyName.Pause, "Pause (dedicated)"),
    ];

    /// <summary>Friendly labels for mouse targets, in <see cref="MouseTarget"/> declaration order.</summary>
    public static IReadOnlyList<(MouseTarget Target, string Label)> MouseTargetChoices { get; } =
    [
        (MouseTarget.BottomRight, "Bottom right"),
        (MouseTarget.BottomLeft, "Bottom left"),
        (MouseTarget.TopRight, "Top right"),
        (MouseTarget.TopLeft, "Top left"),
        (MouseTarget.Center, "Centre"),
        (MouseTarget.Custom, "Custom coordinates"),
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

    /// <summary>True iff the staged edits include a setting marked as requiring a bridge restart.</summary>
    public bool NeedsBridgeRestart => BridgeRestartPolicy.RequiresRestart(_baselineConfig, Working);

    /// <summary>True iff <see cref="Validate"/> finds nothing wrong.</summary>
    public bool IsValid => Validate().Count == 0;

    /// <summary>Finds the descriptor with the given id across all categories (throws for an unknown id — ids are compile-time constants).</summary>
    public static SettingDescriptor Describe(string settingId) =>
        Categories.SelectMany(c => c.Settings).First(s => s.Id == settingId);

    /// <summary>
    /// Resolves choice labels for this Settings-window instance. The read-only
    /// display probe is lazy and cached by <see cref="Lazy{T}"/>, so rebuilding
    /// or filtering the page never enumerates hardware a second time.
    /// </summary>
    internal IReadOnlyList<string> GetChoiceLabels(SettingDescriptor setting)
    {
        if (setting.Id != "power-off-action")
        {
            return setting.ChoiceLabels.Count > 0 ? setting.ChoiceLabels : setting.Choices;
        }

        string suffix = _displayPowerCapability.Value switch
        {
            DisplayPowerCapability.AllDdc => string.Empty,
            DisplayPowerCapability.NoDdc => " (this PC: will also enter standby)",
            DisplayPowerCapability.Mixed => " (non-DDC displays stay on)",
            _ => throw new InvalidOperationException("Unknown display-power capability."),
        };
        return
        [
            $"Turn off displays{suffix}",
            $"Pause, then turn off displays{suffix}",
            "Start screensaver",
            "Sleep",
        ];
    }

    internal IReadOnlyList<(string Value, string Label)> GetMdnsInterfaceChoices(bool showAllAdapters = false)
    {
        var choices = new List<(string Value, string Label)>
        {
            ("", "Auto (recommended)"),
        };
        NetworkAdapterInfo[] visibleAdapters =
        [
            .. _networkAdapters.Where(showAllAdapters
                ? SystemNetworkAdapterProvider.IsVisibleWhenShowingAll
                : SystemNetworkAdapterProvider.IsVisibleByDefault),
        ];
        choices.AddRange(visibleAdapters.Select(adapter =>
            (adapter.Name, $"{adapter.Name} — {adapter.Ipv4Address ?? "no IPv4"}")));

        string? saved = Working.MdnsInterface;
        if (string.IsNullOrWhiteSpace(saved)
            || visibleAdapters.Any(adapter => adapter.Name == saved))
        {
            return choices;
        }

        NetworkAdapterInfo? savedAdapter = _networkAdapters.FirstOrDefault(adapter => adapter.Name == saved);
        if (savedAdapter is null)
        {
            choices.Add((saved, $"{saved} (not detected)"));
        }
        else
        {
            choices.Add((saved, $"{saved} — {savedAdapter.Ipv4Address ?? "no IPv4"} (hidden by filter)"));
        }

        return choices;
    }

    /// <summary>Validates the working copy; empty = saveable.</summary>
    public IReadOnlyList<SettingsValidationError> Validate()
    {
        List<SettingsValidationError> errors = [];
        if (Working.IpcPort is < 1 or > 65535)
        {
            errors.Add(new SettingsValidationError("ipc-port", "Port must be between 1 and 65535."));
        }

        if (Working.MomentaryResetMs is < SettingLimits.MomentaryResetMinimumMs
            or > SettingLimits.MomentaryResetMaximumMs)
        {
            errors.Add(new SettingsValidationError(
                "momentary-reset-ms",
                $"Reset delay must be between {SettingLimits.MomentaryResetMinimumMs} and {SettingLimits.MomentaryResetMaximumMs} ms."));
        }

        if (Working.OverlayOpacityPercent is < SettingLimits.OverlayOpacityMinimumPercent
            or > SettingLimits.OverlayOpacityMaximumPercent)
        {
            errors.Add(new SettingsValidationError(
                "overlay-opacity",
                $"Overlay opacity must be between {SettingLimits.OverlayOpacityMinimumPercent} and {SettingLimits.OverlayOpacityMaximumPercent} %."));
        }

        if (string.IsNullOrWhiteSpace(Working.BridgeName) || Working.BridgeName.Length > Config.MaxNameLength)
        {
            errors.Add(new SettingsValidationError(
                "bridge-name",
                $"Bridge name must be non-empty and at most {Config.MaxNameLength} characters."));
        }

        ValidateBuiltinName(errors, "speaker-name", Working.Commands.Speaker.Name);
        ValidateBuiltinName(errors, "play-pause-name", Working.Commands.PlayPause.Name);
        ValidateBuiltinName(errors, "next-name", Working.Commands.Next.Name);
        ValidateBuiltinName(errors, "previous-name", Working.Commands.Previous.Name);
        ValidateBuiltinName(errors, "power-name", Working.Commands.Power.Name);

        if (Working.Commands.Custom.Count > Config.MaxCustomCommands)
        {
            errors.Add(new SettingsValidationError(
                "custom-commands",
                $"At most {Config.MaxCustomCommands} custom commands are supported."));
        }

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

            if (command.ResetAfterActivation && command.Action is MouseMoveActionConfig)
            {
                errors.Add(new SettingsValidationError(
                    "custom-commands",
                    $"\"{command.Key}\": Move mouse must remain a retained switch so Off can restore the pointer."));
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
        LaunchActionConfig launch => ValidateLaunchPath(launch.Path) ?? ValidateLaunchArguments(launch.Args),
        KeySequenceActionConfig keySequence => ValidateKeySequence(keySequence.Sequence),
        MouseMoveActionConfig { Target: MouseTarget.Custom, X: null } => "Custom mouse target needs an X coordinate.",
        MouseMoveActionConfig { Target: MouseTarget.Custom, Y: null } => "Custom mouse target needs a Y coordinate.",
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

    /// <summary>Validates a custom command's display name (non-empty and config-safe). Null = valid.</summary>
    public static string? ValidateCustomCommandName(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? "Name must not be empty."
            : name.Length > Config.MaxNameLength
                ? $"Name must be at most {Config.MaxNameLength} characters."
                : null;

    /// <summary>Validates a launch action's arguments against the persisted-config cap. Null = valid.</summary>
    public static string? ValidateLaunchArguments(string args) =>
        args.Length > Config.MaxLaunchArgsLength
            ? $"Arguments must be at most {Config.MaxLaunchArgsLength} characters."
            : null;

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
        MouseMoveActionConfig mouseMove => mouseMove.Target == MouseTarget.Custom
            ? $"Move mouse: {mouseMove.X},{mouseMove.Y}"
            : $"Move mouse: {MouseTargetChoices.First(c => c.Target == mouseMove.Target).Label.ToLowerInvariant()}",
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
    /// Saves the staged edits without mutating the live config first, then
    /// reloads so <c>Config.Changed</c> fires and the
    /// existing subscribers (tray checkboxes → bridge/overlay) apply what can
    /// apply live. Throws if <see cref="Validate"/> fails — the window keeps
    /// Save disabled while invalid. Returns false when persistence failed;
    /// <see cref="Working"/> and the baseline remain untouched and dirty.
    /// </summary>
    public bool Apply()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException("cannot apply an invalid working copy");
        }

        if (!_config.Save(Working))
        {
            return false;
        }

        _config.Reload();
        Revert();
        return true;
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
        ReconcileUnchangedFields(Working, _baselineConfig, live);

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
        if (string.IsNullOrWhiteSpace(name) || name.Length > Config.MaxNameLength)
        {
            errors.Add(new SettingsValidationError(
                settingId,
                $"Name must be non-empty and at most {Config.MaxNameLength} characters."));
        }
    }

    /// <summary>Deep copy via a JSON round-trip — the config DTOs are exactly the JSON document, so this is lossless (incl. the polymorphic actions).</summary>
    private static BridgeConfig Clone(BridgeConfig source) =>
        JsonSerializer.Deserialize(Snapshot(source), ConfigJsonContext.Default.BridgeConfig)!;

    /// <summary>Canonical serialized form used for dirty comparison (property order is fixed by the DTO declarations, so equality is well-defined).</summary>
    private static string Snapshot(BridgeConfig config) =>
        JsonSerializer.Serialize(config, ConfigJsonContext.Default.BridgeConfig);

    private static void ReconcileUnchangedFields(BridgeConfig working, BridgeConfig baseline, BridgeConfig live)
    {
        Reconcile(working.IpcPort, baseline.IpcPort, live.IpcPort, value => working.IpcPort = value);
        Reconcile(working.MomentaryResetMs, baseline.MomentaryResetMs, live.MomentaryResetMs, value => working.MomentaryResetMs = value);
        Reconcile(working.PowerOffAction, baseline.PowerOffAction, live.PowerOffAction, value => working.PowerOffAction = value);
        Reconcile(working.OverlayEnabled, baseline.OverlayEnabled, live.OverlayEnabled, value => working.OverlayEnabled = value);
        Reconcile(working.OverlayPosition, baseline.OverlayPosition, live.OverlayPosition, value => working.OverlayPosition = value);
        Reconcile(working.OverlayTheme, baseline.OverlayTheme, live.OverlayTheme, value => working.OverlayTheme = value);
        Reconcile(working.OverlayOpacityPercent, baseline.OverlayOpacityPercent, live.OverlayOpacityPercent, value => working.OverlayOpacityPercent = value);
        Reconcile(working.BridgeEnabled, baseline.BridgeEnabled, live.BridgeEnabled, value => working.BridgeEnabled = value);
        Reconcile(working.UpdateCheckEnabled, baseline.UpdateCheckEnabled, live.UpdateCheckEnabled, value => working.UpdateCheckEnabled = value);
        Reconcile(working.MdnsInterface, baseline.MdnsInterface, live.MdnsInterface, value => working.MdnsInterface = value);
        Reconcile(working.VendorId, baseline.VendorId, live.VendorId, value => working.VendorId = value);
        Reconcile(working.ProductId, baseline.ProductId, live.ProductId, value => working.ProductId = value);
        Reconcile(working.OnboardingShown, baseline.OnboardingShown, live.OnboardingShown, value => working.OnboardingShown = value);
        Reconcile(working.BridgeName, baseline.BridgeName, live.BridgeName, value => working.BridgeName = value);
        Reconcile(working.UniqueIdSeed, baseline.UniqueIdSeed, live.UniqueIdSeed, value => working.UniqueIdSeed = value);
        Reconcile(working.LogLevel, baseline.LogLevel, live.LogLevel, value => working.LogLevel = value);
        Reconcile(working.AppLogLevel, baseline.AppLogLevel, live.AppLogLevel, value => working.AppLogLevel = value);

        ReconcileBuiltin(working.Commands.Speaker, baseline.Commands.Speaker, live.Commands.Speaker);
        ReconcileBuiltin(working.Commands.PlayPause, baseline.Commands.PlayPause, live.Commands.PlayPause);
        ReconcileBuiltin(working.Commands.Next, baseline.Commands.Next, live.Commands.Next);
        ReconcileBuiltin(working.Commands.Previous, baseline.Commands.Previous, live.Commands.Previous);
        ReconcileBuiltin(working.Commands.Power, baseline.Commands.Power, live.Commands.Power);

        if (BridgeRestartPolicy.CustomCommandsSnapshot(working.Commands.Custom)
            == BridgeRestartPolicy.CustomCommandsSnapshot(baseline.Commands.Custom))
        {
            working.Commands.Custom = Clone(live).Commands.Custom;
        }
    }

    private static void ReconcileBuiltin(BuiltinCommandConfig working, BuiltinCommandConfig baseline, BuiltinCommandConfig live)
    {
        Reconcile(working.Name, baseline.Name, live.Name, value => working.Name = value);
        Reconcile(working.Enabled, baseline.Enabled, live.Enabled, value => working.Enabled = value);
    }

    private static void Reconcile<T>(T working, T baseline, T live, Action<T> adopt)
    {
        if (EqualityComparer<T>.Default.Equals(working, baseline))
        {
            adopt(live);
        }
    }

}
