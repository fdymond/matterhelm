using System.Net;
using MatterHelm.Actions;
using MatterHelm.Sidecar;
using Xunit;

namespace MatterHelm.Tests;

public sealed class BridgeLifecycleCoordinatorTests
{
    [Fact]
    public void AddressInUseSurfacesFaultedWithoutPersistingBridgeDisabled()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = new Config(Path.Combine(directory, "config.json"), (_, _) => { });
            config.Current.BridgeEnabled = true;
            var executor = new FakeExecutor();
            var logs = new List<(string Level, string Message)>();
            using var publisher = new VolumeStatePublisher(executor.GetVolumeState, TimeProvider.System, (_, _) => { });
            using var dispatcher = new BridgeActionDispatcher(config, executor, null, (_, _) => { });
            using var coordinator = new BridgeLifecycleCoordinator(
                config,
                executor,
                new SidecarSpec("missing.exe", [], directory),
                overlaySink: null,
                supervisorOptions: null,
                (level, message) => logs.Add((level, message)),
                Path.Combine(directory, "matter"),
                publisher,
                dispatcher,
                startServer: _ => throw new HttpListenerException(183));

            coordinator.SetEnabled(true);

            Assert.True(config.Current.BridgeEnabled);
            Assert.Equal(BridgeState.Faulted, coordinator.State);
            Assert.Contains(logs, entry =>
                entry.Level == "ERROR"
                && entry.Message.Contains("owned elsewhere", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OtherListenerStartFailureKeepsExistingPersistDisabledBehavior()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = new Config(Path.Combine(directory, "config.json"), (_, _) => { });
            config.Current.BridgeEnabled = true;
            var executor = new FakeExecutor();
            using var publisher = new VolumeStatePublisher(executor.GetVolumeState, TimeProvider.System, (_, _) => { });
            using var dispatcher = new BridgeActionDispatcher(config, executor, null, (_, _) => { });
            using var coordinator = new BridgeLifecycleCoordinator(
                config,
                executor,
                new SidecarSpec("missing.exe", [], directory),
                overlaySink: null,
                supervisorOptions: null,
                (_, _) => { },
                Path.Combine(directory, "matter"),
                publisher,
                dispatcher,
                startServer: _ => throw new HttpListenerException(6));

            coordinator.SetEnabled(true);

            Assert.False(config.Current.BridgeEnabled);
            Assert.Equal(BridgeState.Disabled, coordinator.State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PortChangeRetriesAfterListenerWasOwnedElsewhere()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string configPath = Path.Combine(directory, "config.json");
        try
        {
            var config = new Config(configPath, (_, _) => { });
            config.Current.BridgeEnabled = true;
            Assert.True(config.Save());
            var executor = new FakeExecutor();
            int startAttempts = 0;
            using var secondAttempt = new ManualResetEventSlim();
            using var publisher = new VolumeStatePublisher(executor.GetVolumeState, TimeProvider.System, (_, _) => { });
            using var dispatcher = new BridgeActionDispatcher(config, executor, null, (_, _) => { });
            using var coordinator = new BridgeLifecycleCoordinator(
                config,
                executor,
                new SidecarSpec("missing.exe", [], directory),
                overlaySink: null,
                supervisorOptions: null,
                (_, _) => { },
                Path.Combine(directory, "matter"),
                publisher,
                dispatcher,
                startServer: _ =>
                {
                    if (Interlocked.Increment(ref startAttempts) == 2)
                    {
                        secondAttempt.Set();
                    }

                    throw new HttpListenerException(183);
                });

            coordinator.SetEnabled(true);
            File.WriteAllText(configPath, """{"bridgeEnabled":true,"ipcPort":40123}""");
            config.Reload();

            Assert.True(secondAttempt.Wait(TimeSpan.FromSeconds(5)), "port change did not retry the listener");
            Assert.Equal(2, Volatile.Read(ref startAttempts));
            Assert.True(config.Current.BridgeEnabled);
            Assert.Equal(BridgeState.Faulted, coordinator.State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeExecutor : IActionExecutor
    {
        public event EventHandler<VolumeState>? VolumeChanged
        {
            add { }
            remove { }
        }

        public bool Execute(string name, object? value = null) => true;

        public bool ReleaseDisplayKeepAwake() => true;

        public VolumeState GetVolumeState() => new(50, false);

        public void Dispose()
        {
        }
    }
}
