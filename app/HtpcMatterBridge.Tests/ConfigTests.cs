using System.Text.Json;
using Xunit;

namespace HtpcMatterBridge.Tests;

/// <summary>
/// Behaviour tests for <see cref="Config"/>: defaults-on-missing-file,
/// save/reload round-trip (including the ADR-004 <c>commands</c> section with
/// custom commands), the legacy <c>deviceNames</c> → <c>commands</c>
/// migration, malformed-JSON and malformed-individual-field resilience
/// (partial-file merge, custom-entry drop rules), and the
/// <see cref="Config.Reload"/> event. Every test points the config at a
/// throwaway temp-directory path — never the real user profile (CLAUDE.md
/// ground rule 4).
/// </summary>
public sealed class ConfigTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly TestSupport.LogCapture _log = new();

    public ConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "HtpcMatterBridgeTests", Guid.NewGuid().ToString("N"));
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
            // Best-effort cleanup; a leftover temp dir is not worth failing the test run over.
        }
    }

    private Config NewConfig() => new(_path, _log.Sink);

    [Fact]
    public void MissingFileLoadsSpecMandatedDefaults()
    {
        Assert.False(File.Exists(_path));

        Config config = NewConfig();

        CommandsConfig commands = config.Current.Commands;
        Assert.Equal("HTPC Speaker", commands.Speaker.Name);
        Assert.Equal("HTPC Play Pause", commands.PlayPause.Name);
        Assert.Equal("HTPC Next", commands.Next.Name);
        Assert.Equal("HTPC Previous", commands.Previous.Name);
        Assert.Equal("HTPC Power", commands.Power.Name);
        Assert.All(
            new[] { commands.Speaker, commands.PlayPause, commands.Next, commands.Previous, commands.Power },
            builtin => Assert.True(builtin.Enabled));
        Assert.Empty(commands.Custom);
        Assert.Equal(39531, config.Current.IpcPort);
        Assert.Equal(PowerOffAction.PauseAndDisplaysOff, config.Current.PowerOffAction);
        Assert.True(config.Current.OverlayEnabled);
        Assert.Null(config.Current.MdnsInterface);
        Assert.Equal("info", config.Current.LogLevel);
    }

    [Fact]
    public void MissingFileIsCreatedOnDisk()
    {
        _ = NewConfig();

        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void SaveThenLoadFreshRoundTripsEveryField()
    {
        Config config = NewConfig();
        config.Current.Commands.Speaker.Name = "Living Room Speaker";
        config.Current.Commands.PlayPause.Name = "Living Room Play";
        config.Current.Commands.Next.Name = "Living Room Next";
        config.Current.Commands.Previous.Name = "Living Room Previous";
        config.Current.Commands.Power.Name = "Living Room Power";
        config.Current.Commands.Power.Enabled = false;
        config.Current.Commands.Custom =
        [
            new CustomCommandConfig
            {
                Key = "movie-mode",
                Name = "Movie Mode",
                Action = new LaunchActionConfig { Path = @"C:\apps\kodi.exe", Args = "-fs \"C:\\My Movies\"" },
            },
            new CustomCommandConfig
            {
                Key = "stop-media",
                Name = "HTPC Stop",
                Enabled = false,
                Action = new MediaKeyActionConfig { KeyName = MediaKeyName.Stop },
            },
        ];
        config.Current.IpcPort = 40000;
        config.Current.PowerOffAction = PowerOffAction.Sleep;
        config.Current.OverlayEnabled = false;
        config.Current.MdnsInterface = "Ethernet";
        config.Current.LogLevel = "debug";
        config.Save();

        Config reloaded = NewConfig();

        CommandsConfig commands = reloaded.Current.Commands;
        Assert.Equal("Living Room Speaker", commands.Speaker.Name);
        Assert.Equal("Living Room Play", commands.PlayPause.Name);
        Assert.Equal("Living Room Next", commands.Next.Name);
        Assert.Equal("Living Room Previous", commands.Previous.Name);
        Assert.Equal("Living Room Power", commands.Power.Name);
        Assert.True(commands.Speaker.Enabled);
        Assert.False(commands.Power.Enabled);
        Assert.Equal(2, commands.Custom.Count);
        CustomCommandConfig movieMode = commands.Custom[0];
        Assert.Equal("movie-mode", movieMode.Key);
        Assert.Equal("Movie Mode", movieMode.Name);
        Assert.True(movieMode.Enabled);
        LaunchActionConfig launch = Assert.IsType<LaunchActionConfig>(movieMode.Action);
        Assert.Equal(@"C:\apps\kodi.exe", launch.Path);
        Assert.Equal("-fs \"C:\\My Movies\"", launch.Args);
        CustomCommandConfig stopMedia = commands.Custom[1];
        Assert.Equal("stop-media", stopMedia.Key);
        Assert.False(stopMedia.Enabled);
        MediaKeyActionConfig mediaKey = Assert.IsType<MediaKeyActionConfig>(stopMedia.Action);
        Assert.Equal(MediaKeyName.Stop, mediaKey.KeyName);
        Assert.Equal(40000, reloaded.Current.IpcPort);
        Assert.Equal(PowerOffAction.Sleep, reloaded.Current.PowerOffAction);
        Assert.False(reloaded.Current.OverlayEnabled);
        Assert.Equal("Ethernet", reloaded.Current.MdnsInterface);
        Assert.Equal("debug", reloaded.Current.LogLevel);
    }

    [Fact]
    public void SavedFileIsCamelCaseWithWireFormNamesEnumValuesAndActionDiscriminators()
    {
        Config config = NewConfig();
        config.Current.Commands.Custom =
        [
            new CustomCommandConfig
            {
                Key = "stop-media",
                Name = "HTPC Stop",
                Action = new MediaKeyActionConfig { KeyName = MediaKeyName.VolumeUp },
            },
        ];
        config.Save();

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_path));
        JsonElement root = document.RootElement;

        Assert.False(root.TryGetProperty("deviceNames", out _)); // superseded key never written
        Assert.True(root.TryGetProperty("commands", out JsonElement commands));
        Assert.True(commands.TryGetProperty("playPause", out JsonElement playPause));
        Assert.True(playPause.TryGetProperty("name", out _));
        Assert.True(playPause.TryGetProperty("enabled", out _));
        Assert.True(commands.TryGetProperty("custom", out JsonElement custom));
        JsonElement entry = Assert.Single(custom.EnumerateArray());
        Assert.Equal("stop-media", entry.GetProperty("key").GetString());
        JsonElement action = entry.GetProperty("action");
        Assert.Equal("mediaKey", action.GetProperty("type").GetString());
        Assert.Equal("volumeUp", action.GetProperty("keyName").GetString());
        Assert.True(root.TryGetProperty("ipcPort", out _));
        Assert.True(root.TryGetProperty("powerOffAction", out JsonElement powerOffAction));
        Assert.Equal("pauseAndDisplaysOff", powerOffAction.GetString());
        Assert.True(root.TryGetProperty("overlayEnabled", out _));
        Assert.True(root.TryGetProperty("mdnsInterface", out _));
        Assert.True(root.TryGetProperty("logLevel", out _));
    }

    [Theory]
    [InlineData("topLeft", OverlayPosition.TopLeft)]
    [InlineData("middleRight", OverlayPosition.MiddleRight)]
    [InlineData("bottomCenter", OverlayPosition.BottomCenter)]
    public void OverlayPositionLoadsEveryWireForm(string wire, OverlayPosition expected)
    {
        File.WriteAllText(_path, $$"""{"overlayPosition":"{{wire}}"}""");
        Assert.Equal(expected, NewConfig().Current.OverlayPosition);
    }

    [Theory]
    [InlineData("\"center\"")]
    [InlineData("\"5\"")]
    [InlineData("3")]
    public void InvalidOverlayPositionFallsBackToBottomCenterWithAWarn(string rawJsonValue)
    {
        File.WriteAllText(_path, $$"""{"overlayPosition":{{rawJsonValue}}}""");
        Config config = NewConfig();
        Assert.Equal(OverlayPosition.BottomCenter, config.Current.OverlayPosition);
        Assert.True(_log.Contains("WARN", "overlayPosition"), "expected a WARN naming overlayPosition");
    }

    [Fact]
    public void OverlayPositionRoundTripsThroughSaveInCamelCase()
    {
        Config config = NewConfig();
        config.Current.OverlayPosition = OverlayPosition.TopRight;
        config.Save();
        Assert.Contains("\"overlayPosition\": \"topRight\"", File.ReadAllText(_path));
        Assert.Equal(OverlayPosition.TopRight, NewConfig().Current.OverlayPosition);
    }

    [Theory]
    [InlineData("verbose")]
    [InlineData("INFO")]
    [InlineData("2")]
    public void UnknownLogLevelFallsBackToDefaultWithAWarn(string level)
    {
        // S4-R RISK-2: the bridge's parser treats an unknown level as fatal —
        // the tray app must never hand one over (sidecar crash-loop).
        File.WriteAllText(_path, $$"""{"logLevel":"{{level}}"}""");
        Config config = NewConfig();
        Assert.Equal("info", config.Current.LogLevel);
        Assert.True(_log.Contains("WARN", "logLevel"), "expected a WARN naming logLevel");
    }

    [Theory]
    [InlineData("silent")]
    [InlineData("trace")]
    [InlineData("fatal")]
    public void EveryPinoLevelTheBridgeAcceptsLoads(string level)
    {
        File.WriteAllText(_path, $$"""{"logLevel":"{{level}}"}""");
        Assert.Equal(level, NewConfig().Current.LogLevel);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ this is broken json")]
    [InlineData("[1,2,3]")]
    public void UnparsableJsonFallsBackToDefaultsAndWarnsInsteadOfCrashing(string malformed)
    {
        File.WriteAllText(_path, malformed);

        Config config = NewConfig();

        Assert.Equal("HTPC Speaker", config.Current.Commands.Speaker.Name);
        Assert.Equal(39531, config.Current.IpcPort);
        Assert.True(_log.Contains("WARN", "config.json"));
    }

    [Fact]
    public void NonObjectRootFallsBackToDefaults()
    {
        File.WriteAllText(_path, "42");

        Config config = NewConfig();

        Assert.Equal(39531, config.Current.IpcPort);
        Assert.True(_log.Contains("WARN", "root is not an object"));
    }

    [Fact]
    public void PartialFileOnlyAppliesItsValidFieldsRestFallBackToDefaults()
    {
        File.WriteAllText(_path, """{"ipcPort": 5000}""");

        Config config = NewConfig();

        Assert.Equal(5000, config.Current.IpcPort);
        Assert.Equal("HTPC Speaker", config.Current.Commands.Speaker.Name);
        Assert.Equal(PowerOffAction.PauseAndDisplaysOff, config.Current.PowerOffAction);
        Assert.True(config.Current.OverlayEnabled);
        Assert.Equal("info", config.Current.LogLevel);
    }

    public sealed class CommandsSection : ConfigTestsBase
    {
        [Fact]
        public void PartialCommandsObjectMergesFieldByFieldAndWarnsOnTheBadOnes()
        {
            WriteConfig("""
                {"commands": {
                    "speaker": {"name": "", "enabled": false},
                    "next": {"name": "Kitchen Next"},
                    "power": "not-an-object"
                }}
                """);

            Config config = NewConfig();

            CommandsConfig commands = config.Current.Commands;
            Assert.Equal("HTPC Speaker", commands.Speaker.Name); // empty -> default
            Assert.False(commands.Speaker.Enabled); // valid -> applied
            Assert.Equal("Kitchen Next", commands.Next.Name); // valid -> applied
            Assert.True(commands.Next.Enabled); // absent -> default
            Assert.Equal("HTPC Previous", commands.Previous.Name); // absent -> default
            Assert.Equal("HTPC Power", commands.Power.Name); // non-object -> default
            Assert.True(Log.Contains("WARN", "commands.speaker.name"));
            Assert.True(Log.Contains("WARN", "commands.power"));
        }

        [Fact]
        public void NonObjectCommandsSectionFallsBackToDefaultsAndWarns()
        {
            WriteConfig("""{"commands": "nope"}""");

            Config config = NewConfig();

            Assert.Equal("HTPC Speaker", config.Current.Commands.Speaker.Name);
            Assert.Empty(config.Current.Commands.Custom);
            Assert.True(Log.Contains("WARN", "\"commands\" is not an object"));
        }

        [Fact]
        public void NonBooleanBuiltinEnabledFallsBackToDefaultAndWarns()
        {
            WriteConfig("""{"commands": {"power": {"enabled": "yes"}}}""");

            Config config = NewConfig();

            Assert.True(config.Current.Commands.Power.Enabled);
            Assert.True(Log.Contains("WARN", "commands.power.enabled"));
        }

        [Fact]
        public void ValidCustomEntriesLoadWithBothActionTypes()
        {
            WriteConfig("""
                {"commands": {"custom": [
                    {"key": "movie-mode", "name": "Movie Mode", "enabled": true,
                     "action": {"type": "launch", "path": "C:\\apps\\kodi.exe", "args": "-fs"}},
                    {"key": "stop-media", "name": "HTPC Stop", "enabled": false,
                     "action": {"type": "mediaKey", "keyName": "stop"}}
                ]}}
                """);

            Config config = NewConfig();

            Assert.Equal(2, config.Current.Commands.Custom.Count);
            CustomCommandConfig movieMode = config.Current.Commands.Custom[0];
            Assert.Equal("movie-mode", movieMode.Key);
            Assert.Equal("Movie Mode", movieMode.Name);
            Assert.True(movieMode.Enabled);
            LaunchActionConfig launch = Assert.IsType<LaunchActionConfig>(movieMode.Action);
            Assert.Equal(@"C:\apps\kodi.exe", launch.Path);
            Assert.Equal("-fs", launch.Args);
            CustomCommandConfig stopMedia = config.Current.Commands.Custom[1];
            Assert.False(stopMedia.Enabled);
            Assert.Equal(MediaKeyName.Stop, Assert.IsType<MediaKeyActionConfig>(stopMedia.Action).KeyName);
        }

        [Theory]
        [InlineData("\"Movie-Mode\"")] // uppercase
        [InlineData("\"movie_mode\"")] // underscore
        [InlineData("\"movie mode\"")] // space
        [InlineData("\"-movie\"")] // leading hyphen
        [InlineData("\"movie--mode\"")] // empty segment
        [InlineData("\"\"")] // empty
        [InlineData("42")] // non-string
        public void CustomEntryWithAnInvalidKeyIsDroppedWithAWarnButSiblingsLoad(string rawKey)
        {
            WriteConfig($$$"""
                {"commands": {"custom": [
                    {"key": {{{rawKey}}}, "name": "Bad", "action": {"type": "mediaKey", "keyName": "stop"}},
                    {"key": "good-one", "name": "Good", "action": {"type": "mediaKey", "keyName": "mute"}}
                ]}}
                """);

            Config config = NewConfig();

            CustomCommandConfig survivor = Assert.Single(config.Current.Commands.Custom);
            Assert.Equal("good-one", survivor.Key);
            Assert.True(Log.Contains("WARN", "commands.custom[0].key"));
        }

        [Fact]
        public void CustomEntryWithAKeyOverMaxLengthIsDropped()
        {
            string key = new('a', CommandKey.MaxLength + 1);
            WriteConfig($$$"""
                {"commands": {"custom": [
                    {"key": "{{{key}}}", "name": "Too Long", "action": {"type": "mediaKey", "keyName": "stop"}}
                ]}}
                """);

            Config config = NewConfig();

            Assert.Empty(config.Current.Commands.Custom);
            Assert.True(Log.Contains("WARN", "commands.custom[0].key"));
        }

        [Fact]
        public void CustomEntryWithADuplicateKeyIsDroppedTheFirstWins()
        {
            WriteConfig("""
                {"commands": {"custom": [
                    {"key": "movie-mode", "name": "First", "action": {"type": "mediaKey", "keyName": "stop"}},
                    {"key": "movie-mode", "name": "Second", "action": {"type": "mediaKey", "keyName": "mute"}}
                ]}}
                """);

            Config config = NewConfig();

            CustomCommandConfig survivor = Assert.Single(config.Current.Commands.Custom);
            Assert.Equal("First", survivor.Name);
            Assert.True(Log.Contains("WARN", "duplicates an earlier key"));
        }

        [Theory]
        [InlineData("""{"type": "shellExec", "path": "C:\\x.exe"}""")] // unknown type
        [InlineData("""{"path": "C:\\x.exe"}""")] // missing type
        [InlineData("\"launch\"")] // action not an object
        public void CustomEntryWithAnUnknownOrMalformedActionIsDroppedNeverHalfLoaded(string rawAction)
        {
            WriteConfig($$$"""
                {"commands": {"custom": [
                    {"key": "movie-mode", "name": "Movie Mode", "action": {{{rawAction}}}}
                ]}}
                """);

            Config config = NewConfig();

            Assert.Empty(config.Current.Commands.Custom);
            Assert.True(Log.Contains("WARN", "commands.custom[0].action"));
        }

        [Fact]
        public void CustomEntryWithAMissingActionIsDropped()
        {
            WriteConfig("""{"commands": {"custom": [{"key": "movie-mode", "name": "Movie Mode"}]}}""");

            Config config = NewConfig();

            Assert.Empty(config.Current.Commands.Custom);
            Assert.True(Log.Contains("WARN", "commands.custom[0].action"));
        }

        [Theory]
        [InlineData("\"rewind\"")] // not in the union
        [InlineData("\"PlayPause\"")] // wrong case
        [InlineData("42")] // non-string
        public void MediaKeyActionWithAnInvalidKeyNameDropsTheEntry(string rawKeyName)
        {
            WriteConfig($$$"""
                {"commands": {"custom": [
                    {"key": "bad-key-name", "action": {"type": "mediaKey", "keyName": {{{rawKeyName}}}}}
                ]}}
                """);

            Config config = NewConfig();

            Assert.Empty(config.Current.Commands.Custom);
            Assert.True(Log.Contains("WARN", "commands.custom[0].action.keyName"));
        }

        [Theory]
        [InlineData("playPause", MediaKeyName.PlayPause)]
        [InlineData("next", MediaKeyName.Next)]
        [InlineData("previous", MediaKeyName.Previous)]
        [InlineData("stop", MediaKeyName.Stop)]
        [InlineData("mute", MediaKeyName.Mute)]
        [InlineData("volumeUp", MediaKeyName.VolumeUp)]
        [InlineData("volumeDown", MediaKeyName.VolumeDown)]
        public void EveryMediaKeyNameInTheUnionParses(string wireName, MediaKeyName expected)
        {
            WriteConfig($$$"""
                {"commands": {"custom": [
                    {"key": "one-key", "action": {"type": "mediaKey", "keyName": "{{{wireName}}}"}}
                ]}}
                """);

            Config config = NewConfig();

            CustomCommandConfig command = Assert.Single(config.Current.Commands.Custom);
            Assert.Equal(expected, Assert.IsType<MediaKeyActionConfig>(command.Action).KeyName);
        }

        [Theory]
        [InlineData("""{"type": "launch", "args": "-fs"}""")] // missing path
        [InlineData("""{"type": "launch", "path": ""}""")] // empty path
        [InlineData("""{"type": "launch", "path": 42}""")] // non-string path
        public void LaunchActionWithoutAUsablePathDropsTheEntry(string rawAction)
        {
            WriteConfig($$$"""
                {"commands": {"custom": [
                    {"key": "movie-mode", "action": {{{rawAction}}}}
                ]}}
                """);

            Config config = NewConfig();

            Assert.Empty(config.Current.Commands.Custom);
            Assert.True(Log.Contains("WARN", "commands.custom[0].action.path"));
        }

        [Fact]
        public void LaunchActionWithNonStringArgsKeepsTheEntryWithNoArguments()
        {
            WriteConfig("""
                {"commands": {"custom": [
                    {"key": "movie-mode", "action": {"type": "launch", "path": "C:\\x.exe", "args": 42}}
                ]}}
                """);

            Config config = NewConfig();

            CustomCommandConfig command = Assert.Single(config.Current.Commands.Custom);
            Assert.Equal("", Assert.IsType<LaunchActionConfig>(command.Action).Args);
            Assert.True(Log.Contains("WARN", "commands.custom[0].action.args"));
        }

        [Fact]
        public void CustomEntryWithABadNameFallsBackToTheKeyAndABadEnabledToTrue()
        {
            WriteConfig("""
                {"commands": {"custom": [
                    {"key": "movie-mode", "name": "", "enabled": "yes",
                     "action": {"type": "mediaKey", "keyName": "stop"}}
                ]}}
                """);

            Config config = NewConfig();

            CustomCommandConfig command = Assert.Single(config.Current.Commands.Custom);
            Assert.Equal("movie-mode", command.Name);
            Assert.True(command.Enabled);
            Assert.True(Log.Contains("WARN", "commands.custom[0].name"));
            Assert.True(Log.Contains("WARN", "commands.custom[0].enabled"));
        }

        [Fact]
        public void MissingNameFallsBackToTheKeySilently()
        {
            WriteConfig("""
                {"commands": {"custom": [
                    {"key": "movie-mode", "action": {"type": "mediaKey", "keyName": "stop"}}
                ]}}
                """);

            Config config = NewConfig();

            Assert.Equal("movie-mode", Assert.Single(config.Current.Commands.Custom).Name);
        }

        [Fact]
        public void NonArrayCustomFallsBackToEmptyAndWarns()
        {
            WriteConfig("""{"commands": {"custom": {"key": "movie-mode"}}}""");

            Config config = NewConfig();

            Assert.Empty(config.Current.Commands.Custom);
            Assert.True(Log.Contains("WARN", "\"commands.custom\" is not an array"));
        }

        [Fact]
        public void NonObjectCustomEntryIsDroppedWithAWarn()
        {
            WriteConfig("""{"commands": {"custom": ["movie-mode"]}}""");

            Config config = NewConfig();

            Assert.Empty(config.Current.Commands.Custom);
            Assert.True(Log.Contains("WARN", "commands.custom[0]"));
        }
    }

    public sealed class DeviceNamesMigration : ConfigTestsBase
    {
        [Fact]
        public void LegacyDeviceNamesLoadIntoCommandsBuiltinsEnabled()
        {
            WriteConfig("""
                {"deviceNames": {"speaker": "Living Speaker", "playPause": "Living Play",
                 "next": "Living Next", "previous": "Living Previous", "power": "Living Power"},
                 "ipcPort": 5000}
                """);

            Config config = NewConfig();

            CommandsConfig commands = config.Current.Commands;
            Assert.Equal("Living Speaker", commands.Speaker.Name);
            Assert.Equal("Living Play", commands.PlayPause.Name);
            Assert.Equal("Living Next", commands.Next.Name);
            Assert.Equal("Living Previous", commands.Previous.Name);
            Assert.Equal("Living Power", commands.Power.Name);
            Assert.All(
                new[] { commands.Speaker, commands.PlayPause, commands.Next, commands.Previous, commands.Power },
                builtin => Assert.True(builtin.Enabled));
            Assert.Empty(commands.Custom);
            Assert.Equal(5000, config.Current.IpcPort); // non-command fields untouched by migration
        }

        [Fact]
        public void MigrationRewritesTheFileInTheNewShapeDroppingTheOldKey()
        {
            WriteConfig("""{"deviceNames": {"speaker": "Living Speaker"}, "ipcPort": 5000}""");

            _ = NewConfig();

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            JsonElement root = document.RootElement;
            Assert.False(root.TryGetProperty("deviceNames", out _));
            Assert.True(root.TryGetProperty("commands", out JsonElement commands));
            Assert.Equal("Living Speaker", commands.GetProperty("speaker").GetProperty("name").GetString());
            Assert.Equal(5000, root.GetProperty("ipcPort").GetInt32());
            Assert.True(Log.Contains("INFO", "migrated"));
        }

        [Fact]
        public void MigratedFileLoadsIdenticallyOnTheNextFreshLoad()
        {
            WriteConfig("""{"deviceNames": {"next": "Kitchen Next"}}""");

            _ = NewConfig(); // migrates + rewrites
            Config reloaded = NewConfig(); // reads the new shape

            Assert.Equal("Kitchen Next", reloaded.Current.Commands.Next.Name);
            Assert.Equal("HTPC Speaker", reloaded.Current.Commands.Speaker.Name);
        }

        [Fact]
        public void PartialLegacyDeviceNamesMigrateFieldByFieldAndWarnOnTheEmptyOne()
        {
            WriteConfig("""{"deviceNames": {"speaker": "", "next": "Kitchen Next"}}""");

            Config config = NewConfig();

            Assert.Equal("HTPC Speaker", config.Current.Commands.Speaker.Name); // empty -> default
            Assert.Equal("Kitchen Next", config.Current.Commands.Next.Name); // valid -> applied
            Assert.Equal("HTPC Previous", config.Current.Commands.Previous.Name); // absent -> default
            Assert.True(Log.Contains("WARN", "deviceNames.speaker"));
        }

        [Fact]
        public void NonObjectLegacyDeviceNamesStillMigratesToDefaultsAndDropsTheDeadKey()
        {
            WriteConfig("""{"deviceNames": "nope"}""");

            Config config = NewConfig();

            Assert.Equal("HTPC Speaker", config.Current.Commands.Speaker.Name);
            Assert.True(Log.Contains("WARN", "deviceNames"));
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            Assert.False(document.RootElement.TryGetProperty("deviceNames", out _));
        }

        [Fact]
        public void CommandsSectionWinsWhenBothSectionsArePresent()
        {
            WriteConfig("""
                {"deviceNames": {"speaker": "Legacy Speaker"},
                 "commands": {"speaker": {"name": "New Speaker"}}}
                """);

            Config config = NewConfig();

            Assert.Equal("New Speaker", config.Current.Commands.Speaker.Name);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("\"not-a-number\"")]
    public void OutOfRangeOrWrongTypedPortFallsBackToDefaultAndWarns(string rawPortValue)
    {
        File.WriteAllText(_path, $$"""{"ipcPort": {{rawPortValue}}}""");

        Config config = NewConfig();

        Assert.Equal(39531, config.Current.IpcPort);
        Assert.True(_log.Contains("WARN", "ipcPort"));
    }

    [Fact]
    public void UnknownPowerOffActionFallsBackToDefaultAndWarns()
    {
        File.WriteAllText(_path, """{"powerOffAction": "nonsense"}""");

        Config config = NewConfig();

        Assert.Equal(PowerOffAction.PauseAndDisplaysOff, config.Current.PowerOffAction);
        Assert.True(_log.Contains("WARN", "powerOffAction"));
    }

    [Fact]
    public void NullMdnsInterfaceIsAcceptedAsAutoDetect()
    {
        File.WriteAllText(_path, """{"mdnsInterface": null}""");

        Config config = NewConfig();

        Assert.Null(config.Current.MdnsInterface);
    }

    [Fact]
    public void NonBooleanOverlayEnabledFallsBackToDefaultAndWarns()
    {
        File.WriteAllText(_path, """{"overlayEnabled": "yes"}""");

        Config config = NewConfig();

        Assert.True(config.Current.OverlayEnabled);
        Assert.True(_log.Contains("WARN", "overlayEnabled"));
    }

    [Fact]
    public void ReloadPicksUpAnExternalEditWithoutReconstructingConfig()
    {
        Config config = NewConfig();
        Assert.Equal(39531, config.Current.IpcPort);

        File.WriteAllText(_path, """{"ipcPort": 9999}""");
        config.Reload();

        Assert.Equal(9999, config.Current.IpcPort);
    }

    [Fact]
    public void ReloadRaisesChangedExactlyOnceWithOldAndNewConfig()
    {
        Config config = NewConfig();
        var raised = new List<(BridgeConfig Old, BridgeConfig New)>();
        config.Changed += (_, e) => raised.Add((e.OldConfig, e.NewConfig));

        File.WriteAllText(_path, """{"ipcPort": 12345, "overlayEnabled": false}""");
        config.Reload();

        (BridgeConfig old, BridgeConfig updated) = Assert.Single(raised);
        Assert.Equal(39531, old.IpcPort); // the defaults that were in effect before Reload
        Assert.Equal(12345, updated.IpcPort);
        Assert.False(updated.OverlayEnabled);
        Assert.Same(updated, config.Current);
    }

    [Fact]
    public void BridgeEnabledDefaultsToFalseSoFirstRunNeedsAnExplicitEnable()
    {
        Config config = NewConfig();

        Assert.False(config.Current.BridgeEnabled);
    }

    [Fact]
    public void BridgeEnabledRoundTripsThroughSaveAndIsWrittenCamelCase()
    {
        Config config = NewConfig();
        config.Current.BridgeEnabled = true;
        config.Save();

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_path));
        Assert.True(document.RootElement.TryGetProperty("bridgeEnabled", out JsonElement bridgeEnabled));
        Assert.True(bridgeEnabled.GetBoolean());

        Config reloaded = NewConfig();
        Assert.True(reloaded.Current.BridgeEnabled);
    }

    [Fact]
    public void NonBooleanBridgeEnabledFallsBackToDefaultAndWarns()
    {
        File.WriteAllText(_path, """{"bridgeEnabled": "on"}""");

        Config config = NewConfig();

        Assert.False(config.Current.BridgeEnabled);
        Assert.True(_log.Contains("WARN", "bridgeEnabled"));
    }

    [Fact]
    public void ReloadAfterMalformedEditFallsBackToDefaultsWithoutCrashing()
    {
        Config config = NewConfig();
        config.Current.IpcPort = 1234;
        config.Save();

        File.WriteAllText(_path, "not json");
        config.Reload();

        Assert.Equal(39531, config.Current.IpcPort);
    }
}

/// <summary>Shared temp-dir plumbing for the nested <see cref="ConfigTests"/> suites (each nested class is its own xunit collection, so it needs its own fixture).</summary>
public abstract class ConfigTestsBase : IDisposable
{
    private readonly string _dir;

    protected ConfigTestsBase()
    {
        _dir = Path.Combine(Path.GetTempPath(), "HtpcMatterBridgeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ConfigPath = Path.Combine(_dir, "config.json");
    }

    /// <summary>Path of the throwaway config file for this test.</summary>
    protected string ConfigPath { get; }

    /// <summary>Captured log entries from the injected sink.</summary>
    private protected TestSupport.LogCapture Log { get; } = new();

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

        GC.SuppressFinalize(this);
    }

    /// <summary>Writes raw JSON to the throwaway config path.</summary>
    protected void WriteConfig(string json) => File.WriteAllText(ConfigPath, json);

    /// <summary>Creates a <see cref="Config"/> over the throwaway path with the captured log sink.</summary>
    protected Config NewConfig() => new(ConfigPath, Log.Sink);
}
