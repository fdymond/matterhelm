using MatterHelm.Ui;
using MatterHelm.Updates;
using Xunit;

namespace MatterHelm.Tests;

public static class TrayContextTests
{
    private const string SampleQrPayload = "MT:Y.K90C0R159FZO62N10";

    [Theory]
    [InlineData(BridgeState.Disabled)]
    [InlineData(BridgeState.Running)]
    [InlineData(BridgeState.AwaitingPairing)]
    [InlineData(BridgeState.Faulted)]
    public static void UncommissionedStatesShowPairAndFactoryResetButHideEnable(BridgeState state)
    {
        string dir = CreateTempDirectory();
        try
        {
            var config = new Config(Path.Combine(dir, "config.json"), (_, _) => { });
            using var tray = new TrayContext(config);

            tray.SetState(state);

            Assert.True(tray.PairMenuVisible);
            Assert.True(tray.PairMenuEnabled);
            Assert.False(tray.EnableBridgeMenuVisible);
            Assert.True(tray.FactoryResetMenuVisible);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(BridgeState.Connected)]
    [InlineData(BridgeState.Disabled)]
    [InlineData(BridgeState.Running)]
    [InlineData(BridgeState.Faulted)]
    public static void CommissionedStatePersistsAcrossBridgeVariantsAndShowsEnable(BridgeState afterCommissioning)
    {
        string dir = CreateTempDirectory();
        try
        {
            var config = new Config(Path.Combine(dir, "config.json"), (_, _) => { });
            using var tray = new TrayContext(config);
            tray.SetState(BridgeState.Connected);

            tray.SetState(afterCommissioning);

            Assert.False(tray.PairMenuVisible);
            Assert.False(tray.PairMenuEnabled);
            Assert.True(tray.EnableBridgeMenuVisible);
            Assert.True(tray.FactoryResetMenuVisible);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public static void PairedDisabledColdStartUsesExistingFabricSignalToShowEnable()
    {
        string dir = CreateTempDirectory();
        try
        {
            var config = new Config(Path.Combine(dir, "config.json"), (_, _) => { });
            using var tray = new TrayContext(config, commissionedAtStartup: true);

            Assert.Equal(BridgeState.Disabled, tray.State);
            Assert.True(tray.EnableBridgeMenuVisible);
            Assert.False(tray.PairMenuVisible);
            Assert.True(tray.FactoryResetMenuVisible);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public static void PairMenuClickPersistsAndRaisesEnableBeforeOpeningPairing()
    {
        string dir = CreateTempDirectory();
        try
        {
            var config = new Config(Path.Combine(dir, "config.json"), (_, _) => { });
            using var tray = new TrayContext(config);
            var events = new List<string>();
            tray.EnableBridgeChanged += (_, enabled) => events.Add($"enable:{enabled}");
            tray.PairRequested += (_, _) => events.Add("pair");

            tray.PerformPairMenuClick();

            Assert.True(config.Current.BridgeEnabled);
            Assert.True(tray.BridgeMenuChecked);
            Assert.True(tray.PairingWindowOpen);
            Assert.Equal(["enable:True", "pair"], events);

            var reloaded = new Config(Path.Combine(dir, "config.json"), (_, _) => { });
            Assert.True(reloaded.Current.BridgeEnabled);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public static void PairMenuClickRetriesStartWhenPersistedEnabledBridgeIsStopped()
    {
        string dir = CreateTempDirectory();
        try
        {
            var config = new Config(Path.Combine(dir, "config.json"), (_, _) => { });
            config.Current.BridgeEnabled = true;
            config.Save();
            using var tray = new TrayContext(config);
            var requestedStates = new List<bool>();
            tray.EnableBridgeChanged += (_, enabled) => requestedStates.Add(enabled);

            tray.SetState(BridgeState.Disabled);
            tray.PerformPairMenuClick();

            Assert.Equal([true], requestedStates);
            Assert.True(tray.PairingWindowOpen);
            Assert.Contains("bridge off", tray.PairingWindowTitle, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public static void CommissioningAndFactoryResetFlipTheContextualMenuLive()
    {
        string dir = CreateTempDirectory();
        try
        {
            var config = new Config(Path.Combine(dir, "config.json"), (_, _) => { });
            using var tray = new TrayContext(config);
            tray.SetState(BridgeState.AwaitingPairing);

            tray.SetState(BridgeState.Connected);
            Assert.True(tray.EnableBridgeMenuVisible);
            Assert.False(tray.PairMenuVisible);

            config.Current.BridgeEnabled = true;
            tray.OnFactoryResetCompleted(new FactoryResetResult(true, null));
            Assert.False(tray.EnableBridgeMenuVisible);
            Assert.True(tray.PairMenuVisible);
            Assert.True(tray.FactoryResetMenuVisible);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public static void CommissionedWhileReadyShowsPairedSchedulesCloseAndThenCloses()
    {
        string dir = CreateTempDirectory();
        try
        {
            Action? scheduledClose = null;
            TimeSpan scheduledDelay = TimeSpan.Zero;
            var config = new Config(Path.Combine(dir, "config.json"), (_, _) => { });
            using var tray = new TrayContext(
                config,
                pairingAutoCloseScheduler: (callback, delay) =>
                {
                    scheduledClose = callback;
                    scheduledDelay = delay;
                });
            tray.StartGuidedPairing();
            tray.SetState(BridgeState.AwaitingPairing);
            tray.SetPairingInfo(SampleQrPayload, "0434-914-6415");
            Assert.Equal(PairingStage.ReadyToScan, tray.PairingWindowStage);

            tray.SetState(BridgeState.Connected);

            Assert.Equal(PairingStage.Paired, tray.PairingWindowStage);
            Assert.True(tray.PairingWindowAutoCloseScheduled);
            Assert.Equal(TimeSpan.FromSeconds(4), scheduledDelay);

            Assert.IsType<Action>(scheduledClose)();

            Assert.False(tray.PairingWindowOpen);
            Assert.Null(tray.PairingWindowStage);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public static void CommissionedWhileClosedDoesNotOpenPairingWindow()
    {
        string dir = CreateTempDirectory();
        try
        {
            var config = new Config(Path.Combine(dir, "config.json"), (_, _) => { });
            using var tray = new TrayContext(config);

            tray.SetState(BridgeState.AwaitingPairing);
            Assert.Equal("MatterHelm — running, not paired yet", tray.ToolTipText);
            tray.SetState(BridgeState.Connected);

            Assert.False(tray.PairingWindowOpen);
            Assert.Null(tray.PairingWindowStage);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public static void DisableClearsPairingDataRendersBridgeOffAndKeepsPairActionable()
    {
        string dir = CreateTempDirectory();
        try
        {
            var config = new Config(Path.Combine(dir, "config.json"), (_, _) => { });
            using var tray = new TrayContext(config);
            tray.StartGuidedPairing();
            tray.SetState(BridgeState.AwaitingPairing);
            tray.SetPairingInfo(SampleQrPayload, "0434-914-6415");
            Assert.True(tray.PairMenuEnabled);

            tray.SetState(BridgeState.Disabled);

            Assert.True(tray.PairMenuEnabled);
            Assert.True(tray.PairMenuVisible);
            Assert.Equal(PairingStage.Starting, tray.PairingWindowStage);
            Assert.Contains("bridge off", tray.PairingWindowTitle, StringComparison.Ordinal);

            tray.SetPairingInfo("MT:LATE", "1111-222-3333");
            tray.SetState(BridgeState.AwaitingPairing);
            Assert.Equal(PairingStage.Starting, tray.PairingWindowStage);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public static void FactoryResetFailureAlwaysSurfacesAndResyncsTheEnabledState()
    {
        string dir = CreateTempDirectory();
        try
        {
            var config = new Config(Path.Combine(dir, "config.json"), (_, _) => { });
            var errors = new List<(string Title, string Message)>();
            using var tray = new TrayContext(
                config,
                showError: (title, message) => errors.Add((title, message)));
            config.Current.BridgeEnabled = true;

            tray.OnFactoryResetCompleted(new FactoryResetResult(false, "storage is locked"));

            Assert.True(tray.BridgeMenuChecked);
            (string title, string message) = Assert.Single(errors);
            Assert.Equal("Factory reset failed", title);
            Assert.Contains("storage is locked", message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public static async Task UpdateHandoffStartsOnlyOnceAfterWindowsAreClosed()
    {
        string dir = CreateTempDirectory();
        try
        {
            int starts = 0;
            var helperStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var config = new Config(Path.Combine(dir, "config.json"), (_, _) => { });
            using var tray = new TrayContext(
                config,
                startUpdateHandoff: (_, _, _, _, _) =>
                {
                    Interlocked.Increment(ref starts);
                    return helperStarted.Task;
                });
            UpdateRelease release = TestRelease();
            var probe = new FakeInstallationProbe();

            Task<bool> first = tray.StartUpdateHandoffAfterClosingWindowsAsync(
                release, "package.zip", new string('a', 64), probe);
            bool second = await tray.StartUpdateHandoffAfterClosingWindowsAsync(
                release, "package.zip", new string('a', 64), probe);

            Assert.False(second);
            Assert.Equal(1, Volatile.Read(ref starts));
            helperStarted.SetResult(true);
            Assert.True(await first);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static UpdateRelease TestRelease() => new(
        new SemanticVersion(9, 9, 9),
        "v9.9.9",
        UpdateInstallMode.Portable,
        new UpdateAsset("matterhelm-v9.9.9-win-x64.zip", new Uri("https://example.test/package")),
        new UpdateAsset("SHA256SUMS", new Uri("https://example.test/checksums")));

    private sealed class FakeInstallationProbe : IUpdateInstallationProbe
    {
        public IReadOnlyList<UpdateUninstallRegistration> InnoUninstallRegistrations => [];

        public string ExecutablePath => Path.Combine(Path.GetTempPath(), "MatterHelm.exe");

        public bool FileExists(string path) => false;
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "MatterHelm-TrayContextTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
