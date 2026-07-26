using System.Text.Json;
using Xunit;

namespace HtpcMatterBridge.Tests;

/// <summary>
/// Behaviour tests for <see cref="Config"/>: defaults-on-missing-file,
/// save/reload round-trip, malformed-JSON and malformed-individual-field
/// resilience (partial-file merge), and the <see cref="Config.Reload"/>
/// event. Every test points the config at a throwaway temp-directory path —
/// never the real user profile (CLAUDE.md ground rule 4).
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

        Assert.Equal("HTPC Speaker", config.Current.DeviceNames.Speaker);
        Assert.Equal("HTPC Play Pause", config.Current.DeviceNames.PlayPause);
        Assert.Equal("HTPC Next", config.Current.DeviceNames.Next);
        Assert.Equal("HTPC Previous", config.Current.DeviceNames.Previous);
        Assert.Equal("HTPC Power", config.Current.DeviceNames.Power);
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
        config.Current.DeviceNames.Speaker = "Living Room Speaker";
        config.Current.DeviceNames.PlayPause = "Living Room Play";
        config.Current.DeviceNames.Next = "Living Room Next";
        config.Current.DeviceNames.Previous = "Living Room Previous";
        config.Current.DeviceNames.Power = "Living Room Power";
        config.Current.IpcPort = 40000;
        config.Current.PowerOffAction = PowerOffAction.Sleep;
        config.Current.OverlayEnabled = false;
        config.Current.MdnsInterface = "Ethernet";
        config.Current.LogLevel = "debug";
        config.Save();

        Config reloaded = NewConfig();

        Assert.Equal("Living Room Speaker", reloaded.Current.DeviceNames.Speaker);
        Assert.Equal("Living Room Play", reloaded.Current.DeviceNames.PlayPause);
        Assert.Equal("Living Room Next", reloaded.Current.DeviceNames.Next);
        Assert.Equal("Living Room Previous", reloaded.Current.DeviceNames.Previous);
        Assert.Equal("Living Room Power", reloaded.Current.DeviceNames.Power);
        Assert.Equal(40000, reloaded.Current.IpcPort);
        Assert.Equal(PowerOffAction.Sleep, reloaded.Current.PowerOffAction);
        Assert.False(reloaded.Current.OverlayEnabled);
        Assert.Equal("Ethernet", reloaded.Current.MdnsInterface);
        Assert.Equal("debug", reloaded.Current.LogLevel);
    }

    [Fact]
    public void SavedFileIsCamelCaseWithWireFormNamesAndEnumValue()
    {
        Config config = NewConfig();
        config.Save();

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_path));
        JsonElement root = document.RootElement;

        Assert.True(root.TryGetProperty("deviceNames", out JsonElement deviceNames));
        Assert.True(deviceNames.TryGetProperty("playPause", out _));
        Assert.True(root.TryGetProperty("ipcPort", out _));
        Assert.True(root.TryGetProperty("powerOffAction", out JsonElement powerOffAction));
        Assert.Equal("pauseAndDisplaysOff", powerOffAction.GetString());
        Assert.True(root.TryGetProperty("overlayEnabled", out _));
        Assert.True(root.TryGetProperty("mdnsInterface", out _));
        Assert.True(root.TryGetProperty("logLevel", out _));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ this is broken json")]
    [InlineData("[1,2,3]")]
    public void UnparsableJsonFallsBackToDefaultsAndWarnsInsteadOfCrashing(string malformed)
    {
        File.WriteAllText(_path, malformed);

        Config config = NewConfig();

        Assert.Equal("HTPC Speaker", config.Current.DeviceNames.Speaker);
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
        Assert.Equal("HTPC Speaker", config.Current.DeviceNames.Speaker);
        Assert.Equal(PowerOffAction.PauseAndDisplaysOff, config.Current.PowerOffAction);
        Assert.True(config.Current.OverlayEnabled);
        Assert.Equal("info", config.Current.LogLevel);
    }

    [Fact]
    public void PartialDeviceNamesObjectMergesFieldByFieldAndWarnsOnTheEmptyOne()
    {
        File.WriteAllText(_path, """{"deviceNames": {"speaker": "", "next": "Kitchen Next"}}""");

        Config config = NewConfig();

        Assert.Equal("HTPC Speaker", config.Current.DeviceNames.Speaker); // empty -> default
        Assert.Equal("Kitchen Next", config.Current.DeviceNames.Next); // valid -> applied
        Assert.Equal("HTPC Previous", config.Current.DeviceNames.Previous); // absent -> default
        Assert.True(_log.Contains("WARN", "deviceNames.speaker"));
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
