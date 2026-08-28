using System.Net;
using System.Net.NetworkInformation;
using MatterHelm.Actions;
using MatterHelm.Ui;
using Xunit;
using Xunit.Abstractions;

namespace MatterHelm.Tests;

/// <summary>
/// Behaviour tests for <see cref="SettingsViewModel"/> (the settings window's
/// logic per ADR-004 §4): staged working-copy isolation, dirty tracking,
/// validation rules (port range, non-empty names, custom-key slug/uniqueness/
/// reserved-name rules, launch-path existence), custom-command CRUD, the
/// descriptor schema (get/set round-trips, restart flags, unique ids), and the
/// Apply → save → reload round-trip against a throwaway temp config — never
/// the real user profile (CLAUDE.md ground rule 4).
/// </summary>
public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly TestSupport.LogCapture _log = new();
    private readonly ITestOutputHelper _output;

    public SettingsViewModelTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "config.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private Config NewConfig() => new(_path, _log.Sink);

    private SettingsViewModel NewViewModel(
        Config? config = null,
        Func<string, bool>? pathExists = null,
        INetworkAdapterProvider? networkAdapters = null,
        IDisplayPowerCapabilityProbe? displayPowerCapabilityProbe = null) =>
        new(
            config ?? NewConfig(),
            pathExists,
            networkAdapters ?? new StubNetworkAdapterProvider([]),
            displayPowerCapabilityProbe ?? new StubDisplayPowerCapabilityProbe(DisplayPowerCapability.AllDdc));

    private static CustomCommandConfig MediaKeyCommand(string key, string? name = null) => new()
    {
        Key = key,
        Name = name ?? key,
        Action = new MediaKeyActionConfig { KeyName = MediaKeyName.Stop },
    };

    // ---- external live-config changes (S4-R RISK-1) ------------------------

    [Fact]
    public void ExternalChangeWhileNotDirtyRestagesTheWorkingCopy()
    {
        Config config = NewConfig();
        SettingsViewModel vm = NewViewModel(config);

        // A tray toggle persists + reloads outside the window.
        config.Current.BridgeEnabled = true;
        config.Save();
        config.Reload();

        vm.AbsorbExternalConfigChange();

        Assert.True(vm.Working.BridgeEnabled);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void ExternalChangeWhileDirtyPreservesEditsButSyncsUntouchedLiveToggles()
    {
        Config config = NewConfig();
        SettingsViewModel vm = NewViewModel(config);

        // The user has staged an unrelated edit (still dirty afterwards)...
        vm.Working.Commands.Speaker.Name = "Renamed Speaker";

        // ...while the tray toggles "Enable bridge" externally.
        config.Current.BridgeEnabled = true;
        config.Save();
        config.Reload();

        vm.AbsorbExternalConfigChange();

        // The untouched live toggle syncs (Save can no longer revert the tray
        // action), the staged rename survives, and the copy stays dirty.
        Assert.True(vm.Working.BridgeEnabled);
        Assert.Equal("Renamed Speaker", vm.Working.Commands.Speaker.Name);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void ExternalChangeNeverClobbersAToggleTheUserAlreadyEdited()
    {
        Config config = NewConfig();
        SettingsViewModel vm = NewViewModel(config);

        // The user staged BridgeEnabled=true themselves; an external reload
        // that still has it false must not undo their staged intent.
        vm.Working.BridgeEnabled = true;
        config.Save();
        config.Reload();

        vm.AbsorbExternalConfigChange();

        Assert.True(vm.Working.BridgeEnabled);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void ExternalChangeThreeWayMergesEveryUntouchedFieldBeforeApply()
    {
        Config config = NewConfig();
        SettingsViewModel vm = NewViewModel(config);
        vm.Working.Commands.Speaker.Name = "My staged speaker";

        var external = new Config(_path, _log.Sink);
        external.Current.IpcPort = 40123;
        external.Current.OverlayPosition = OverlayPosition.TopLeft;
        external.Current.Commands.Next.Name = "Externally renamed next";
        external.Current.Commands.Custom.Add(MediaKeyCommand("external-command"));
        external.Current.UpdateCheckEnabled = false;
        external.Current.OnboardingShown = true;
        Assert.True(external.Save());
        config.Reload();

        vm.AbsorbExternalConfigChange();

        Assert.Equal("My staged speaker", vm.Working.Commands.Speaker.Name);
        Assert.Equal(40123, vm.Working.IpcPort);
        Assert.Equal(OverlayPosition.TopLeft, vm.Working.OverlayPosition);
        Assert.Equal("Externally renamed next", vm.Working.Commands.Next.Name);
        Assert.Equal("external-command", Assert.Single(vm.Working.Commands.Custom).Key);
        Assert.False(vm.Working.UpdateCheckEnabled);
        Assert.True(vm.Working.OnboardingShown);
        Assert.True(vm.IsDirty);

        Assert.True(vm.Apply());
        Config persisted = NewConfig();
        Assert.Equal("My staged speaker", persisted.Current.Commands.Speaker.Name);
        Assert.Equal(40123, persisted.Current.IpcPort);
        Assert.Equal("Externally renamed next", persisted.Current.Commands.Next.Name);
        Assert.Equal("external-command", Assert.Single(persisted.Current.Commands.Custom).Key);
        Assert.False(persisted.Current.UpdateCheckEnabled);
        Assert.True(persisted.Current.OnboardingShown);
    }

    // ---- staging / dirty tracking -----------------------------------------

    [Fact]
    public void WorkingCopyIsADeepCloneEditingItNeverTouchesTheLiveConfig()
    {
        Config config = NewConfig();
        SettingsViewModel vm = NewViewModel(config);

        vm.Working.IpcPort = 40000;
        vm.Working.Commands.Speaker.Name = "Changed";
        vm.AddCustomCommand(MediaKeyCommand("new-one"));

        Assert.Equal(39531, config.Current.IpcPort);
        Assert.Equal("HTPC Speaker", config.Current.Commands.Speaker.Name);
        Assert.Empty(config.Current.Commands.Custom);
    }

    [Fact]
    public void FreshViewModelIsCleanAndValid()
    {
        SettingsViewModel vm = NewViewModel();

        Assert.False(vm.IsDirty);
        Assert.True(vm.IsValid);
        Assert.Empty(vm.Validate());
    }

    [Fact]
    public void AnyEditMakesItDirtyAndRevertRestoresClean()
    {
        SettingsViewModel vm = NewViewModel();

        vm.Working.OverlayEnabled = false;
        Assert.True(vm.IsDirty);

        vm.Revert();
        Assert.False(vm.IsDirty);
        Assert.True(vm.Working.OverlayEnabled);
    }

    // ---- validation --------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void OutOfRangePortFailsValidationOnTheIpcPortRow(int port)
    {
        SettingsViewModel vm = NewViewModel();

        vm.Working.IpcPort = port;

        SettingsValidationError error = Assert.Single(vm.Validate());
        Assert.Equal("ipc-port", error.SettingId);
    }

    [Fact]
    public void EmptyBuiltinNameFailsValidationOnItsOwnRow()
    {
        SettingsViewModel vm = NewViewModel();

        vm.Working.Commands.PlayPause.Name = "  ";

        SettingsValidationError error = Assert.Single(vm.Validate());
        Assert.Equal("play-pause-name", error.SettingId);
    }

    [Fact]
    public void DuplicateCustomKeysFailValidationOnTheCustomCommandsRow()
    {
        SettingsViewModel vm = NewViewModel();

        vm.AddCustomCommand(MediaKeyCommand("movie-mode"));
        vm.AddCustomCommand(MediaKeyCommand("movie-mode"));

        SettingsValidationError error = Assert.Single(vm.Validate());
        Assert.Equal("custom-commands", error.SettingId);
        Assert.Contains("already uses this key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomKeyCollidingWithABuiltinRoleNameFailsValidation()
    {
        SettingsViewModel vm = NewViewModel();

        vm.AddCustomCommand(MediaKeyCommand("power"));

        SettingsValidationError error = Assert.Single(vm.Validate());
        Assert.Equal("custom-commands", error.SettingId);
        Assert.Contains("reserved", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyCustomCommandNameFailsValidation()
    {
        SettingsViewModel vm = NewViewModel();

        vm.AddCustomCommand(MediaKeyCommand("movie-mode", name: " "));

        SettingsValidationError error = Assert.Single(vm.Validate());
        Assert.Equal("custom-commands", error.SettingId);
    }

    [Fact]
    public void LaunchCommandWithAMissingPathFailsValidationButAnExistingOnePasses()
    {
        SettingsViewModel vm = NewViewModel(pathExists: path => path == @"C:\exists.exe");

        vm.AddCustomCommand(new CustomCommandConfig
        {
            Key = "movie-mode",
            Name = "Movie Mode",
            Action = new LaunchActionConfig { Path = @"C:\missing.exe" },
        });
        SettingsValidationError error = Assert.Single(vm.Validate());
        Assert.Equal("custom-commands", error.SettingId);
        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);

        ((LaunchActionConfig)vm.Working.Commands.Custom[0].Action).Path = @"C:\exists.exe";
        Assert.True(vm.IsValid);
    }

    [Fact]
    public void ValidateActionAcceptsAWellFormedSequence()
    {
        SettingsViewModel vm = NewViewModel(pathExists: _ => true);
        var sequence = new SequenceActionConfig
        {
            Steps =
            [
                new MediaKeyActionConfig { KeyName = MediaKeyName.Stop },
                new DelayActionConfig { Ms = 250 },
                new KeySequenceActionConfig { Sequence = "Ctrl+Shift+V" },
            ],
        };

        Assert.Null(vm.ValidateAction(sequence, allowSequence: true));
    }

    [Fact]
    public void ValidateActionRejectsANestedSequenceAndNamesTheStep()
    {
        SettingsViewModel vm = NewViewModel();
        var sequence = new SequenceActionConfig
        {
            Steps =
            [
                new MediaKeyActionConfig { KeyName = MediaKeyName.Stop },
                new SequenceActionConfig { Steps = [new MediaKeyActionConfig { KeyName = MediaKeyName.Next }] },
            ],
        };

        string? error = vm.ValidateAction(sequence, allowSequence: true);
        Assert.NotNull(error);
        Assert.StartsWith("Step 2:", error, StringComparison.Ordinal);
        Assert.Contains("cannot contain another sequence", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateActionRejectsAnEmptySequenceAndASummedDelayOverTheCap()
    {
        SettingsViewModel vm = NewViewModel();

        Assert.Contains("1–16 steps", vm.ValidateAction(
            new SequenceActionConfig { Steps = [] }, allowSequence: true), StringComparison.Ordinal);

        var overCap = new SequenceActionConfig
        {
            Steps =
            [
                new DelayActionConfig { Ms = 5000 },
                new DelayActionConfig { Ms = 5000 },
                new DelayActionConfig { Ms = 1 },
            ],
        };
        Assert.Contains("cap is 10000 ms", vm.ValidateAction(overCap, allowSequence: true), StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeActionSummarizesSystemCommands()
    {
        Assert.Equal("System: start screensaver", SettingsViewModel.DescribeAction(
            new SystemActionConfig { Command = SystemCommandName.StartScreenSaver }));
        Assert.Equal("System: close focused program", SettingsViewModel.DescribeAction(
            new SystemActionConfig { Command = SystemCommandName.CloseForegroundProgram }));
    }

    [Fact]
    public void MediaKeyChoicesCoverEveryEnumMemberInOrder()
    {
        // The dialogs index this list by combo position, so it must stay in
        // declaration order and complete (S9-1 added Play/Pause).
        Assert.Equal(
            Enum.GetValues<MediaKeyName>(),
            SettingsViewModel.MediaKeyChoices.Select(c => c.Key));
    }

    [Fact]
    public void SystemCommandChoicesCoverEveryEnumMemberInOrder()
    {
        // The dialogs index this list by combo position, so it must stay in
        // declaration order and complete.
        Assert.Equal(
            Enum.GetValues<SystemCommandName>(),
            SettingsViewModel.SystemCommandChoices.Select(c => c.Command));
    }

    [Fact]
    public void DescribeActionSummarizesSequencesAndDelays()
    {
        Assert.Equal("Wait: 250 ms", SettingsViewModel.DescribeAction(new DelayActionConfig { Ms = 250 }));
        Assert.Equal("Sequence: 1 step", SettingsViewModel.DescribeAction(
            new SequenceActionConfig { Steps = [new DelayActionConfig { Ms = 1 }] }));
        Assert.Equal("Sequence: 2 steps", SettingsViewModel.DescribeAction(
            new SequenceActionConfig
            {
                Steps = [new DelayActionConfig { Ms = 1 }, new MediaKeyActionConfig { KeyName = MediaKeyName.Stop }],
            }));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2001)]
    public void OutOfRangeMomentaryResetFailsValidationOnItsOwnRow(int ms)
    {
        SettingsViewModel vm = NewViewModel();

        vm.Working.MomentaryResetMs = ms;

        SettingsValidationError error = Assert.Single(vm.Validate());
        Assert.Equal("momentary-reset-ms", error.SettingId);
    }

    [Fact]
    public void InvalidKeySequenceCommandFailsValidationOnTheCustomCommandsRow()
    {
        SettingsViewModel vm = NewViewModel();

        vm.AddCustomCommand(new CustomCommandConfig
        {
            Key = "paste-plain",
            Name = "Paste Plain",
            Action = new KeySequenceActionConfig { Sequence = "Ctrl+Bogus" },
        });

        SettingsValidationError error = Assert.Single(vm.Validate());
        Assert.Equal("custom-commands", error.SettingId);
        Assert.Contains("not a known key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidKeySequenceCommandPasses()
    {
        SettingsViewModel vm = NewViewModel(pathExists: _ => false);

        vm.AddCustomCommand(new CustomCommandConfig
        {
            Key = "paste-plain",
            Name = "Paste Plain",
            Action = new KeySequenceActionConfig { Sequence = "Ctrl+Shift+V" },
        });

        Assert.True(vm.IsValid);
    }

    [Fact]
    public void MediaKeyCommandsNeedNoPathAndPass()
    {
        SettingsViewModel vm = NewViewModel(pathExists: _ => false);

        vm.AddCustomCommand(MediaKeyCommand("movie-mode"));

        Assert.True(vm.IsValid);
    }

    // ---- ValidateCustomCommand* (the dialog's inline rules) ----------------

    [Theory]
    [InlineData("Movie-Mode")]
    [InlineData("movie_mode")]
    [InlineData("movie mode")]
    [InlineData("-movie")]
    [InlineData("")]
    public void InvalidSlugKeysAreRejectedWithTheSlugMessage(string key)
    {
        SettingsViewModel vm = NewViewModel();

        string? error = vm.ValidateCustomCommandKey(key);

        Assert.NotNull(error);
        Assert.Contains("kebab-case", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("speaker")]
    [InlineData("next")]
    [InlineData("previous")]
    [InlineData("power")]
    public void BuiltinRoleNamesAreReservedKeys(string key)
    {
        SettingsViewModel vm = NewViewModel();

        string? error = vm.ValidateCustomCommandKey(key);

        Assert.NotNull(error);
        Assert.Contains("reserved", error, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyUniquenessExcludesTheCommandBeingEdited()
    {
        SettingsViewModel vm = NewViewModel();
        vm.AddCustomCommand(MediaKeyCommand("movie-mode"));
        vm.AddCustomCommand(MediaKeyCommand("stop-media"));

        Assert.NotNull(vm.ValidateCustomCommandKey("movie-mode")); // adding a duplicate
        Assert.Null(vm.ValidateCustomCommandKey("movie-mode", originalKey: "movie-mode")); // editing itself
        Assert.NotNull(vm.ValidateCustomCommandKey("stop-media", originalKey: "movie-mode")); // renaming onto a sibling
        Assert.Null(vm.ValidateCustomCommandKey("fresh-key"));
    }

    [Fact]
    public void NameAndLaunchPathRulesMatchTheAggregateValidation()
    {
        SettingsViewModel vm = NewViewModel(pathExists: _ => false);

        Assert.NotNull(SettingsViewModel.ValidateCustomCommandName(""));
        Assert.Null(SettingsViewModel.ValidateCustomCommandName("Movie Mode"));
        Assert.NotNull(vm.ValidateLaunchPath(""));
        Assert.NotNull(vm.ValidateLaunchPath(@"C:\missing.exe"));
    }

    [Fact]
    public void KeySequenceRuleMirrorsTheKeyChordParser()
    {
        // The dialog validates its sequence TextBox through this rule (S7-1).
        Assert.Null(SettingsViewModel.ValidateKeySequence("ctrl+shift+v"));
        Assert.Null(SettingsViewModel.ValidateKeySequence("F5"));
        Assert.NotNull(SettingsViewModel.ValidateKeySequence(""));
        Assert.NotNull(SettingsViewModel.ValidateKeySequence("Ctrl+Ctrl+V"));
        Assert.NotNull(SettingsViewModel.ValidateKeySequence("Ctrl+Bogus"));
    }

    // ---- custom command CRUD ----------------------------------------------

    [Fact]
    public void UpdateReplacesInPlacePreservingListOrder()
    {
        SettingsViewModel vm = NewViewModel();
        vm.AddCustomCommand(MediaKeyCommand("first-one"));
        vm.AddCustomCommand(MediaKeyCommand("second-one"));

        vm.UpdateCustomCommand("first-one", MediaKeyCommand("first-one", name: "Renamed"));

        Assert.Equal(["first-one", "second-one"], vm.Working.Commands.Custom.Select(c => c.Key));
        Assert.Equal("Renamed", vm.Working.Commands.Custom[0].Name);
    }

    [Fact]
    public void UpdateOfAnUnknownKeyThrows()
    {
        SettingsViewModel vm = NewViewModel();

        Assert.Throws<InvalidOperationException>(
            () => vm.UpdateCustomCommand("no-such", MediaKeyCommand("no-such")));
    }

    [Fact]
    public void RemoveDeletesByKeyAndDirtiesTheModel()
    {
        SettingsViewModel vm = NewViewModel();
        vm.AddCustomCommand(MediaKeyCommand("movie-mode"));
        vm.AddCustomCommand(MediaKeyCommand("stop-media"));

        vm.RemoveCustomCommand("movie-mode");

        Assert.Equal("stop-media", Assert.Single(vm.Working.Commands.Custom).Key);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void DescribeActionSummarizesEveryActionType()
    {
        Assert.Equal(
            "Media key: stop",
            SettingsViewModel.DescribeAction(new MediaKeyActionConfig { KeyName = MediaKeyName.Stop }));
        Assert.Equal(
            "Launch: kodi.exe",
            SettingsViewModel.DescribeAction(new LaunchActionConfig { Path = @"C:\apps\kodi.exe" }));
        Assert.Equal(
            "Key sequence: Ctrl+Shift+V",
            SettingsViewModel.DescribeAction(new KeySequenceActionConfig { Sequence = "Ctrl+Shift+V" }));
    }

    // ---- apply / revert / reload ------------------------------------------

    [Fact]
    public void ApplyPersistsEveryStagedEditToDiskAndAFreshLoadRoundTrips()
    {
        Config config = NewConfig();
        SettingsViewModel vm = NewViewModel(config);

        vm.Working.IpcPort = 40000;
        vm.Working.MomentaryResetMs = 450;
        vm.Working.Commands.Speaker.Name = "Demo Speaker";
        vm.Working.Commands.Power.Enabled = false;
        vm.Working.PowerOffAction = PowerOffAction.Screensaver;
        vm.Working.MdnsInterface = "Ethernet";
        vm.Working.LogLevel = "debug";
        vm.Working.AppLogLevel = "warn";
        // Regression (S5-2 report): CopyInto silently dropped OverlayPosition.
        vm.Working.OverlayPosition = OverlayPosition.TopRight;
        vm.AddCustomCommand(MediaKeyCommand("demo-cmd", name: "Demo Command"));
        vm.Apply();

        Assert.False(vm.IsDirty);
        Assert.Equal(40000, config.Current.IpcPort);

        Config reloaded = NewConfig();
        Assert.Equal(40000, reloaded.Current.IpcPort);
        Assert.Equal(450, reloaded.Current.MomentaryResetMs);
        Assert.Equal("Demo Speaker", reloaded.Current.Commands.Speaker.Name);
        Assert.False(reloaded.Current.Commands.Power.Enabled);
        Assert.Equal(PowerOffAction.Screensaver, reloaded.Current.PowerOffAction);
        Assert.Equal("Ethernet", reloaded.Current.MdnsInterface);
        Assert.Equal("debug", reloaded.Current.LogLevel);
        Assert.Equal("warn", reloaded.Current.AppLogLevel);
        Assert.Equal(OverlayPosition.TopRight, reloaded.Current.OverlayPosition);
        CustomCommandConfig custom = Assert.Single(reloaded.Current.Commands.Custom);
        Assert.Equal("demo-cmd", custom.Key);
        Assert.Equal("Demo Command", custom.Name);
        Assert.Equal(MediaKeyName.Stop, Assert.IsType<MediaKeyActionConfig>(custom.Action).KeyName);
    }

    [Fact]
    public void ApplyFiresTheExistingChangedMachineryExactlyOnce()
    {
        Config config = NewConfig();
        SettingsViewModel vm = NewViewModel(config);
        var raised = new List<ConfigChangedEventArgs>();
        config.Changed += (_, e) => raised.Add(e);

        vm.Working.BridgeEnabled = true;
        vm.Apply();

        ConfigChangedEventArgs change = Assert.Single(raised);
        Assert.True(change.NewConfig.BridgeEnabled);
    }

    [Fact]
    public void ApplyOnAnInvalidWorkingCopyThrowsAndPersistsNothing()
    {
        Config config = NewConfig();
        SettingsViewModel vm = NewViewModel(config);

        vm.Working.IpcPort = 0;

        Assert.Throws<InvalidOperationException>(() => vm.Apply());
        Assert.Equal(39531, config.Current.IpcPort);
    }

    [Fact]
    public void SaveFailureStaysDirtyLeavesLiveConfigUntouchedAndSurfacesAnError()
    {
        var config = new Config(_dir, _log.Sink);
        SettingsViewModel vm = NewViewModel(config);
        vm.Working.IpcPort = 40123;
        var errors = new List<(string Title, string Message)>();
        using var window = new SettingsWindow(
            vm,
            showError: (title, message) => errors.Add((title, message)));

        bool saved = window.SaveNow();

        Assert.False(saved);
        Assert.True(vm.IsDirty);
        Assert.Equal(39531, config.Current.IpcPort);
        (string title, string message) = Assert.Single(errors);
        Assert.Equal("Settings could not be saved", title);
        Assert.Contains("Your edits are still here", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReloadFromDiskPicksUpExternalEditsAndDiscardsStagedOnes()
    {
        Config config = NewConfig();
        SettingsViewModel vm = NewViewModel(config);
        vm.Working.LogLevel = "trace"; // staged, never saved

        File.WriteAllText(_path, """{"ipcPort": 12345}""");
        vm.ReloadFromDisk();

        Assert.False(vm.IsDirty);
        Assert.Equal(12345, vm.Working.IpcPort);
        Assert.Equal("info", vm.Working.LogLevel);
    }

    // ---- descriptor schema -------------------------------------------------

    [Fact]
    public void CategoriesMatchTheAdr004Section4Layout()
    {
        Assert.Equal(
            ["General", "Devices", "Custom devices", "Overlay", "Advanced"],
            SettingsViewModel.Categories.Select(c => c.Title));
    }

    [Fact]
    public void SettingIdsAreUniqueAcrossAllCategories()
    {
        var ids = SettingsViewModel.Categories.SelectMany(c => c.Settings).Select(s => s.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ValueSettingsAllRoundTripThroughTheirGetAndSet()
    {
        SettingsViewModel vm = NewViewModel();
        Dictionary<string, object?> samples = new()
        {
            ["bridge-enabled"] = true,
            ["ipc-port"] = 40000,
            ["log-level"] = "debug",
            ["app-log-level"] = "warn",
            ["speaker-name"] = "S",
            ["play-pause-name"] = "PP",
            ["next-name"] = "N",
            ["previous-name"] = "P",
            ["power-name"] = "PW",
            ["power-off-action"] = "sleep",
            ["momentary-reset-ms"] = 500,
            ["overlay-enabled"] = false,
            ["overlay-position"] = "topRight",
            ["overlay-theme"] = "light",
            ["overlay-opacity"] = 80,
            ["mdns-interface"] = "Ethernet",
            ["bridge-name"] = "Office Bridge",
            ["vendor-id"] = "0xFFF2",
            ["product-id"] = "0x8003",
        };

        foreach (SettingDescriptor setting in SettingsViewModel.Categories.SelectMany(c => c.Settings))
        {
            if (setting.Set is null)
            {
                continue;
            }

            object? sample = samples[setting.Id]; // throws if a new editable setting lacks a sample
            setting.Set(vm.Working, sample);
            Assert.Equal(sample, setting.Get!(vm.Working));

            // S9-2 compact command rows carry a second value pair.
            if (setting.SetEnabled is not null)
            {
                setting.SetEnabled(vm.Working, false);
                Assert.False(setting.GetEnabled!(vm.Working));
                setting.SetEnabled(vm.Working, true);
                Assert.True(setting.GetEnabled(vm.Working));
            }
        }

        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void EveryCommandRowCarriesTheEnabledPairAndNothingElseDoes()
    {
        foreach (SettingDescriptor setting in SettingsViewModel.Categories.SelectMany(c => c.Settings))
        {
            bool isCommandRow = setting.Kind == SettingKind.CommandRow;
            Assert.Equal(isCommandRow, setting.GetEnabled is not null);
            Assert.Equal(isCommandRow, setting.SetEnabled is not null);
        }
    }

    [Fact]
    public void EmptyMdnsInterfaceTextMapsBackToNullAutoDetect()
    {
        SettingsViewModel vm = NewViewModel();
        SettingDescriptor mdns = SettingsViewModel.Describe("mdns-interface");

        mdns.Set!(vm.Working, "  ");

        Assert.Null(vm.Working.MdnsInterface);
        Assert.Equal("", mdns.Get!(vm.Working));
    }

    [Fact]
    public void MdnsInterfaceChoicesMapAdapterNamesToAnnotatedDisplayLabels()
    {
        var adapters = new StubNetworkAdapterProvider(
        [
            new NetworkAdapterInfo("Ethernet", "192.0.2.20"),
            new NetworkAdapterInfo("Wi-Fi", null),
        ]);
        SettingsViewModel vm = NewViewModel(networkAdapters: adapters);

        Assert.Equal(
        [
            ("", "Auto (recommended)"),
            ("Ethernet", "Ethernet — 192.0.2.20"),
            ("Wi-Fi", "Wi-Fi — no IPv4"),
        ],
            vm.GetMdnsInterfaceChoices());

        SettingDescriptor mdns = SettingsViewModel.Describe("mdns-interface");
        mdns.Set!(vm.Working, "Wi-Fi");
        Assert.Equal("Wi-Fi", vm.Working.MdnsInterface);
        Assert.Equal("Wi-Fi", mdns.Get!(vm.Working));
    }

    [Fact]
    public void AutoMdnsChoicePersistsAsNullAndReadsBackAsEmpty()
    {
        Config config = NewConfig();
        config.Current.MdnsInterface = "Ethernet";
        SettingsViewModel vm = NewViewModel(
            config,
            networkAdapters: new StubNetworkAdapterProvider([new NetworkAdapterInfo("Ethernet", "203.0.113.5")]));
        SettingDescriptor mdns = SettingsViewModel.Describe("mdns-interface");

        mdns.Set!(vm.Working, "");

        Assert.Null(vm.Working.MdnsInterface);
        Assert.Equal("", mdns.Get!(vm.Working));
        Assert.True(vm.Apply());
        Assert.Null(NewConfig().Current.MdnsInterface);
    }

    [Fact]
    public void SavedUndetectedMdnsInterfaceRemainsASelectableStaleChoice()
    {
        Config config = NewConfig();
        config.Current.MdnsInterface = "USB Ethernet";
        SettingsViewModel vm = NewViewModel(
            config,
            networkAdapters: new StubNetworkAdapterProvider([new NetworkAdapterInfo("Ethernet", "192.0.2.20")]));

        IReadOnlyList<(string Value, string Label)> choices = vm.GetMdnsInterfaceChoices();
        Assert.Equal(
            ("USB Ethernet", "USB Ethernet (not detected)"),
            choices[choices.Count - 1]);
        Assert.Equal("USB Ethernet", vm.Working.MdnsInterface);

        SettingsViewModel.Describe("mdns-interface").Set!(vm.Working, choices[0].Value);
        Assert.Null(vm.Working.MdnsInterface);
    }

    [Fact]
    public void SavedDetectedAdapterHiddenByDefaultRemainsVisibleAndExplainsWhy()
    {
        Config config = NewConfig();
        config.Current.MdnsInterface = "vEthernet (WSL)";
        SettingsViewModel vm = NewViewModel(
            config,
            networkAdapters: new StubNetworkAdapterProvider(
            [
                new NetworkAdapterInfo(
                    "vEthernet (WSL)",
                    "203.0.113.161",
                    Description: "Hyper-V Virtual Ethernet Adapter"),
            ]));

        Assert.Equal(
            ("vEthernet (WSL)", "vEthernet (WSL) — 203.0.113.161 (hidden by filter)"),
            vm.GetMdnsInterfaceChoices()[1]);
        Assert.Equal(
            ("vEthernet (WSL)", "vEthernet (WSL) — 203.0.113.161"),
            vm.GetMdnsInterfaceChoices(showAllAdapters: true)[1]);
        Assert.Equal("vEthernet (WSL)", vm.Working.MdnsInterface);
    }

    [Fact]
    public void ShowAllFallbackRestoresThePreviousInclusiveAdapterList()
    {
        SettingsViewModel vm = NewViewModel(
            networkAdapters: new StubNetworkAdapterProvider(
            [
                new NetworkAdapterInfo("Ethernet", "192.0.2.20", "Realtek PCIe GbE"),
                new NetworkAdapterInfo("Work VPN", null, "TAP-Windows Adapter V9", InterfaceType: NetworkInterfaceType.Ppp, HasIpUnicastAddress: false),
                new NetworkAdapterInfo("Loopback", "127.0.0.1", "Software Loopback Interface", InterfaceType: NetworkInterfaceType.Loopback),
                new NetworkAdapterInfo("Disconnected", "203.0.113.102", "USB Ethernet", OperationalStatus.Down),
            ]));

        Assert.Equal(
            ["Auto (recommended)", "Ethernet — 192.0.2.20"],
            vm.GetMdnsInterfaceChoices().Select(choice => choice.Label));
        Assert.Equal(
            ["Auto (recommended)", "Ethernet — 192.0.2.20", "Work VPN — no IPv4"],
            vm.GetMdnsInterfaceChoices(showAllAdapters: true).Select(choice => choice.Label));
    }

    [Fact]
    public void ShowAllCheckboxIsUncheckedUiOnlyAndRefreshesTheDropdown()
    {
        SettingsViewModel vm = NewViewModel(
            networkAdapters: new StubNetworkAdapterProvider(
            [
                new NetworkAdapterInfo("Ethernet", "192.0.2.20", "Realtek PCIe GbE"),
                new NetworkAdapterInfo("Work VPN", "203.0.113.2", "TAP-Windows Adapter V9"),
            ]));
        using var window = new SettingsWindow(vm);
        var combo = Assert.IsType<ComboBox>(Assert.Single(window.Controls.Find("mdns-interface-choice", searchAllChildren: true)));
        var showAll = Assert.IsType<CheckBox>(Assert.Single(window.Controls.Find("mdns-show-all-adapters", searchAllChildren: true)));

        Assert.False(showAll.Checked);
        Assert.DoesNotContain("Work VPN — 203.0.113.2", combo.Items.Cast<string>());

        showAll.Checked = true;

        Assert.Contains("Work VPN — 203.0.113.2", combo.Items.Cast<string>());
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void NoisyAdapterSetRendersOnlyTheRealLanUntilShowAllIsSelected()
    {
        SettingsViewModel vm = NewViewModel(
            networkAdapters: new StubNetworkAdapterProvider(
            [
                new NetworkAdapterInfo("Ethernet", "198.51.100.12", "Realtek PCIe GbE", HasIpv4DefaultGateway: true),
                new NetworkAdapterInfo("vEthernet (WSL)", "203.0.113.161", "Hyper-V Virtual Ethernet Adapter"),
                new NetworkAdapterInfo("Work VPN", "203.0.113.2", "TAP-Windows Adapter V9"),
                new NetworkAdapterInfo("Bluetooth Network Connection", "169.254.2.4", "Bluetooth Device (Personal Area Network)"),
            ]));

        string filtered = string.Join(" | ", vm.GetMdnsInterfaceChoices().Select(choice => choice.Label));
        string all = string.Join(" | ", vm.GetMdnsInterfaceChoices(showAllAdapters: true).Select(choice => choice.Label));
        _output.WriteLine($"Filtered: {filtered}");
        _output.WriteLine($"Show all: {all}");

        Assert.Equal("Auto (recommended) | Ethernet — 198.51.100.12", filtered);
        Assert.Equal(
            "Auto (recommended) | Ethernet — 198.51.100.12 | vEthernet (WSL) — 203.0.113.161 | Work VPN — 203.0.113.2 | Bluetooth Network Connection — 169.254.2.4",
            all);
    }

    [Fact]
    public void AdapterListIsEnumeratedOnceForEachSettingsViewModel()
    {
        var adapters = new StubNetworkAdapterProvider([new NetworkAdapterInfo("Ethernet", "192.0.2.20")]);

        _ = NewViewModel(networkAdapters: adapters);
        _ = NewViewModel(networkAdapters: adapters);

        Assert.Equal(2, adapters.CallCount);
    }

    [Fact]
    public void ChangingMdnsChoiceRemainsRestartMarked()
    {
        SettingsViewModel vm = NewViewModel(
            networkAdapters: new StubNetworkAdapterProvider([new NetworkAdapterInfo("Wi-Fi", "192.0.2.21")]));
        SettingDescriptor mdns = SettingsViewModel.Describe("mdns-interface");

        Assert.Equal(SettingKind.NetworkAdapterChoice, mdns.Kind);
        Assert.True(mdns.NeedsBridgeRestart);
        mdns.Set!(vm.Working, "Wi-Fi");
        Assert.True(vm.NeedsBridgeRestart);
    }

    [Fact]
    public void PowerOffActionChoiceExposesTheWireNames()
    {
        SettingsViewModel vm = NewViewModel();
        SettingDescriptor choice = SettingsViewModel.Describe("power-off-action");

        Assert.Equal(["displaysOff", "pauseAndDisplaysOff", "screensaver", "sleep"], choice.Choices);
        Assert.Equal(["Turn off displays", "Pause, then turn off displays", "Start screensaver", "Sleep"], vm.GetChoiceLabels(choice));
        Assert.Equal("pauseAndDisplaysOff", choice.Get!(vm.Working));
        choice.Set!(vm.Working, "screensaver");
        Assert.Equal(PowerOffAction.Screensaver, vm.Working.PowerOffAction);
    }

    [Theory]
    [InlineData(
        (int)DisplayPowerCapability.AllDdc,
        "Turn off displays",
        "Pause, then turn off displays")]
    [InlineData(
        (int)DisplayPowerCapability.NoDdc,
        "Turn off displays (this PC: will also enter standby)",
        "Pause, then turn off displays (this PC: will also enter standby)")]
    [InlineData(
        (int)DisplayPowerCapability.Mixed,
        "Turn off displays (non-DDC displays stay on)",
        "Pause, then turn off displays (non-DDC displays stay on)")]
    public void PowerOffActionLabelsDescribeCurrentDdcCapability(
        int capabilityValue,
        string displaysOffLabel,
        string pauseThenDisplaysOffLabel)
    {
        var probe = new StubDisplayPowerCapabilityProbe((DisplayPowerCapability)capabilityValue);
        SettingsViewModel vm = NewViewModel(displayPowerCapabilityProbe: probe);
        SettingDescriptor choice = SettingsViewModel.Describe("power-off-action");

        IReadOnlyList<string> first = vm.GetChoiceLabels(choice);
        IReadOnlyList<string> second = vm.GetChoiceLabels(choice);

        Assert.Equal(displaysOffLabel, first[0]);
        Assert.Equal(pauseThenDisplaysOffLabel, first[1]);
        Assert.Equal(first, second);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public void RestartNoteFlagsSitExactlyOnTheRowsThatNeedABridgeRestart()
    {
        string[] flagged = [.. SettingsViewModel.Categories
            .SelectMany(c => c.Settings)
            .Where(s => s.NeedsBridgeRestart)
            .Select(s => s.Id)];

        Assert.Equal(
            [
                "ipc-port", "log-level",
                "bridge-name",
                "speaker-name", "play-pause-name", "next-name", "previous-name", "power-name",
                "momentary-reset-ms",
                "custom-commands",
                "mdns-interface",
                "vendor-id", "product-id",
            ],
            flagged);
    }

    [Fact]
    public void RestartRequirementTracksOnlyRestartMarkedStagedChanges()
    {
        SettingsViewModel vm = NewViewModel();

        vm.Working.OverlayOpacityPercent = 80;
        Assert.False(vm.NeedsBridgeRestart);

        vm.Working.ProductId++;
        Assert.True(vm.NeedsBridgeRestart);
    }

    [Fact]
    public void StorageDirDisplayIsTheUserProfileMatterDir()
    {
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MatterHelm",
                "matter"),
            SettingsViewModel.StorageDirDisplay);
        Assert.Equal(SettingsViewModel.StorageDirDisplay, SettingsViewModel.Describe("storage-dir").Get!(new BridgeConfig()));
    }

    private sealed class StubNetworkAdapterProvider(IReadOnlyList<NetworkAdapterInfo> adapters) : INetworkAdapterProvider
    {
        internal int CallCount { get; private set; }

        public IReadOnlyList<NetworkAdapterInfo> GetAdapters()
        {
            CallCount++;
            return adapters;
        }
    }

    private sealed class StubDisplayPowerCapabilityProbe(DisplayPowerCapability capability)
        : IDisplayPowerCapabilityProbe
    {
        internal int CallCount { get; private set; }

        public DisplayPowerCapability Probe()
        {
            CallCount++;
            return capability;
        }
    }
}

public sealed class NetworkAdapterEnumerationTests
{
    [Theory]
    [InlineData("Microsoft Hyper-V Network Adapter")]
    [InlineData("Hyper-V Virtual Ethernet Adapter")]
    [InlineData("vEthernet (Default Switch)")]
    [InlineData("WSL virtual network")]
    [InlineData("VMware Virtual Ethernet Adapter")]
    [InlineData("VirtualBox Host-Only Ethernet Adapter")]
    [InlineData("TAP-Windows Adapter V9")]
    [InlineData("Npcap Loopback Adapter")]
    [InlineData("software loopback interface")]
    [InlineData("Bluetooth Device (Personal Area Network)")]
    public void VirtualDescriptionKeywordsAreExcludedCaseInsensitively(string description)
    {
        Assert.False(SystemNetworkAdapterProvider.IsVisibleByDefault(Adapter(description: description)));
    }

    [Fact]
    public void Ipv4DefaultGatewayOutranksAVirtualDescriptionKeyword()
    {
        Assert.True(SystemNetworkAdapterProvider.IsVisibleByDefault(
            Adapter(description: "VMware Ethernet Adapter", hasIpv4DefaultGateway: true)));
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet)]
    [InlineData(NetworkInterfaceType.Ethernet3Megabit)]
    [InlineData(NetworkInterfaceType.FastEthernetT)]
    [InlineData(NetworkInterfaceType.FastEthernetFx)]
    [InlineData(NetworkInterfaceType.GigabitEthernet)]
    [InlineData(NetworkInterfaceType.Wireless80211)]
    public void EthernetFamilyAndWirelessAdaptersAreIncluded(NetworkInterfaceType type)
    {
        Assert.True(SystemNetworkAdapterProvider.IsVisibleByDefault(Adapter(type: type)));
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Tunnel)]
    [InlineData(NetworkInterfaceType.Ppp)]
    [InlineData(NetworkInterfaceType.Loopback)]
    [InlineData(NetworkInterfaceType.Wwanpp)]
    public void TunnelPppLoopbackAndExoticTypesAreExcluded(NetworkInterfaceType type)
    {
        Assert.False(SystemNetworkAdapterProvider.IsVisibleByDefault(Adapter(type: type)));
    }

    [Theory]
    [InlineData(OperationalStatus.Down, true)]
    [InlineData(OperationalStatus.Up, false)]
    public void DefaultListRequiresUpStatusAndAnIpUnicastAddress(
        OperationalStatus status,
        bool hasIpUnicastAddress)
    {
        Assert.False(SystemNetworkAdapterProvider.IsVisibleByDefault(
            Adapter(status: status, hasIpUnicastAddress: hasIpUnicastAddress)));
    }

    [Theory]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Tunnel, true)]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Ppp, true)]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Loopback, false)]
    [InlineData(OperationalStatus.Down, NetworkInterfaceType.Ethernet, false)]
    public void ShowAllMatchesThePreviousUpNonLoopbackRule(
        OperationalStatus status,
        NetworkInterfaceType type,
        bool expected)
    {
        Assert.Equal(expected, SystemNetworkAdapterProvider.IsVisibleWhenShowingAll(
            Adapter(status: status, type: type)));
    }

    [Fact]
    public void FirstIpv4AddressSkipsIpv6AndReturnsNullWhenNoneExists()
    {
        Assert.Equal(
            "198.51.100.12",
            SystemNetworkAdapterProvider.FirstIpv4Address(
            [
                IPAddress.Parse("fe80::1"),
                IPAddress.Parse("198.51.100.12"),
            ]));
        Assert.Null(SystemNetworkAdapterProvider.FirstIpv4Address([IPAddress.Parse("fe80::1")]));
    }

    private static NetworkAdapterInfo Adapter(
        string description = "Realtek PCIe GbE Family Controller",
        OperationalStatus status = OperationalStatus.Up,
        NetworkInterfaceType type = NetworkInterfaceType.Ethernet,
        bool hasIpUnicastAddress = true,
        bool hasIpv4DefaultGateway = false) =>
        new(
            "Ethernet",
            "198.51.100.12",
            description,
            status,
            type,
            hasIpUnicastAddress,
            hasIpv4DefaultGateway);
}
