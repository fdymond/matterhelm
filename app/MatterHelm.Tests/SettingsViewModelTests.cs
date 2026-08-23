using MatterHelm.Ui;
using Xunit;

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

    public SettingsViewModelTests()
    {
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

    private SettingsViewModel NewViewModel(Config? config = null, Func<string, bool>? pathExists = null) =>
        new(config ?? NewConfig(), pathExists);

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
        vm.Working.PowerOffAction = PowerOffAction.Sleep;
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
        Assert.Equal(PowerOffAction.Sleep, reloaded.Current.PowerOffAction);
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

        Assert.Throws<InvalidOperationException>(vm.Apply);
        Assert.Equal(39531, config.Current.IpcPort);
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
            ["General", "Devices & Commands", "Overlay", "Advanced"],
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
            ["mdns-interface"] = "Ethernet",
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
    public void PowerOffActionChoiceExposesTheWireNames()
    {
        SettingsViewModel vm = NewViewModel();
        SettingDescriptor choice = SettingsViewModel.Describe("power-off-action");

        Assert.Equal(["displaysOff", "pauseAndDisplaysOff", "sleep"], choice.Choices);
        Assert.Equal("pauseAndDisplaysOff", choice.Get!(vm.Working));
        choice.Set!(vm.Working, "displaysOff");
        Assert.Equal(PowerOffAction.DisplaysOff, vm.Working.PowerOffAction);
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
                "speaker-name", "play-pause-name", "next-name", "previous-name", "power-name",
                "momentary-reset-ms",
                "custom-commands",
                "mdns-interface",
            ],
            flagged);
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
}
