namespace MatterHelm.Ui;

/// <summary>The declarative settings schema shared by every settings-window instance.</summary>
internal static class SettingsCatalog
{
    internal static IReadOnlyList<SettingsCategory> Categories { get; } = BuildCategories();

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
            Title = "Devices",
            Settings =
            [
                new SettingDescriptor
                {
                    Id = "bridge-name",
                    Label = "Bridge name",
                    Description = "What Google Home calls this PC's bridge — handy when more than one PC runs MatterHelm.",
                    Kind = SettingKind.Text,
                    NeedsBridgeRestart = true,
                    Get = c => c.BridgeName,
                    Set = (c, v) => c.BridgeName = (string)v!,
                },
                new SettingDescriptor
                {
                    Id = "google-home-devices",
                    Label = "Google Home devices",
                    Description = "Each ticked device is published to Google Home under the name you give it.",
                    Kind = SettingKind.SectionHeader,
                },
                BuiltinCommand("speaker-name", "Speaker (volume + mute)", "", c => c.Commands.Speaker),
                BuiltinCommand("play-pause-name", "Play/pause", "", c => c.Commands.PlayPause),
                BuiltinCommand("next-name", "Next track", "", c => c.Commands.Next),
                BuiltinCommand("previous-name", "Previous track", "", c => c.Commands.Previous),
                BuiltinCommand("power-name", "Power", "", c => c.Commands.Power),
                new SettingDescriptor
                {
                    Id = "power-off-action",
                    Label = "Power off behavior",
                    Description = "What turning the power device off does on this PC.",
                    Kind = SettingKind.Choice,
                    Choices = ["displaysOff", "pauseAndDisplaysOff", "screensaver", "sleep"],
                    ChoiceLabels = ["Turn off displays", "Pause, then turn off displays", "Start screensaver", "Sleep"],
                    NeedsBridgeRestart = true,
                    Get = c => ToWireName(c.PowerOffAction),
                    Set = (c, v) => c.PowerOffAction = FromWireName((string)v!),
                },
                new SettingDescriptor
                {
                    Id = "momentary-reset-ms",
                    Label = "Tap reset delay (ms)",
                    Description = "How quickly an opted-in momentary custom command resets to off. 0 = immediately.",
                    Kind = SettingKind.Number,
                    Minimum = SettingLimits.MomentaryResetMinimumMs,
                    Maximum = SettingLimits.MomentaryResetMaximumMs,
                    NeedsBridgeRestart = true,
                    Get = c => c.MomentaryResetMs,
                    Set = (c, v) => c.MomentaryResetMs = (int)v!,
                },
            ],
        },
        new SettingsCategory
        {
            Id = "custom-devices",
            Title = "Custom devices",
            Settings =
            [
                new SettingDescriptor
                {
                    Id = "custom-commands",
                    Label = "Custom devices",
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
                    Id = "overlay-theme",
                    Label = "Overlay theme",
                    Description = "Panel colors: follow the Windows light/dark setting, or force one.",
                    Kind = SettingKind.Choice,
                    Choices = ["system", "dark", "light"],
                    ChoiceLabels = ["Follow system", "Dark", "Light"],
                    Get = c => c.OverlayTheme switch
                    {
                        OverlayTheme.Dark => "dark",
                        OverlayTheme.Light => "light",
                        _ => "system",
                    },
                    Set = (c, v) => c.OverlayTheme = (string)v! switch
                    {
                        "dark" => OverlayTheme.Dark,
                        "light" => OverlayTheme.Light,
                        _ => OverlayTheme.System,
                    },
                },
                new SettingDescriptor
                {
                    Id = "overlay-opacity",
                    Label = "Overlay opacity",
                    Description = "How see-through the overlay panel is.",
                    Kind = SettingKind.Slider,
                    Minimum = SettingLimits.OverlayOpacityMinimumPercent,
                    Maximum = SettingLimits.OverlayOpacityMaximumPercent,
                    Get = c => c.OverlayOpacityPercent,
                    Set = (c, v) => c.OverlayOpacityPercent = (int)v!,
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
                    Description = "Choose where Matter announces this bridge. Auto is recommended unless this PC has multiple adapters.",
                    Kind = SettingKind.NetworkAdapterChoice,
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
                    Get = _ => SettingsViewModel.StorageDirDisplay,
                },
                new SettingDescriptor
                {
                    Id = "vendor-id",
                    Label = "Vendor ID (VID)",
                    Description = "Must match your Google Home Developer Console project. Changing it re-pairs the bridge.",
                    Kind = SettingKind.Text,
                    NeedsBridgeRestart = true,
                    Get = c => MatterIds.Format(c.VendorId),
                    Set = (c, v) =>
                    {
                        if (MatterIds.TryParse((string?)v, out int id))
                        {
                            c.VendorId = id;
                        }
                    },
                },
                new SettingDescriptor
                {
                    Id = "product-id",
                    Label = "Product ID (PID)",
                    Description = "Must match your Google Home Developer Console project. Changing it re-pairs the bridge.",
                    Kind = SettingKind.Text,
                    NeedsBridgeRestart = true,
                    Get = c => MatterIds.Format(c.ProductId),
                    Set = (c, v) =>
                    {
                        if (MatterIds.TryParse((string?)v, out int id))
                        {
                            c.ProductId = id;
                        }
                    },
                },
                new SettingDescriptor
                {
                    Id = "unique-id-seed",
                    Label = "Device identity seed",
                    Description = "This install's Matter identity. Unique per install, so two PCs never collide in one home.",
                    Kind = SettingKind.ReadOnlyText,
                    Get = c => c.UniqueIdSeed ?? "(resolved at next start)",
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

    private static SettingDescriptor BuiltinCommand(
        string id,
        string label,
        string description,
        Func<BridgeConfig, BuiltinCommandConfig> builtin) => new()
    {
        Id = id,
        Label = label,
        Description = description,
        Kind = SettingKind.CommandRow,
        NeedsBridgeRestart = true,
        Get = c => builtin(c).Name,
        Set = (c, v) => builtin(c).Name = (string)v!,
        GetEnabled = c => builtin(c).Enabled,
        SetEnabled = (c, v) => builtin(c).Enabled = v,
    };

    private static string ToWireName(PowerOffAction action) => action switch
    {
        PowerOffAction.DisplaysOff => "displaysOff",
        PowerOffAction.PauseAndDisplaysOff => "pauseAndDisplaysOff",
        PowerOffAction.Screensaver => "screensaver",
        PowerOffAction.Sleep => "sleep",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    private static PowerOffAction FromWireName(string wireName) => wireName switch
    {
        "displaysOff" => PowerOffAction.DisplaysOff,
        "pauseAndDisplaysOff" => PowerOffAction.PauseAndDisplaysOff,
        "screensaver" => PowerOffAction.Screensaver,
        "sleep" => PowerOffAction.Sleep,
        _ => throw new ArgumentOutOfRangeException(nameof(wireName), wireName, null),
    };

    private static string OverlayPositionToWire(OverlayPosition position)
    {
        string name = position.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static OverlayPosition OverlayPositionFromWire(string wireName) =>
        Enum.Parse<OverlayPosition>(wireName, ignoreCase: true);
}
