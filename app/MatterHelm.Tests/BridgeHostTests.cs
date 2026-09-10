using System.Diagnostics.Metrics;
using System.Text.Json;
using MatterHelm.Actions;
using MatterHelm.Diagnostics;
using MatterHelm.Infrastructure;
using MatterHelm.Sidecar;
using MatterHelm.Ui;
using Xunit;

namespace MatterHelm.Tests;

/// <summary>
/// Behaviour tests for <see cref="BridgeHost"/>: the pure tray-state rule, and
/// the full wiring layer against real node stub sidecars speaking the real WS
/// protocol — action → executor (faked) → overlay sink → ack, state frames on
/// connect and on volume change, pairing surfacing, and the crash-loop → red
/// derivation. The executor is faked so no test touches real volume/media/
/// display state; everything else (IpcServer, SidecarSupervisor, sockets,
/// child processes) is real.
/// </summary>
public static class BridgeHostTests
{
    public sealed class StateDerivation
    {
        [Theory]
        [InlineData(false, false, 0, BridgeState.Disabled)]
        [InlineData(false, true, 5, BridgeState.Disabled)]
        [InlineData(true, false, 0, BridgeState.Running)]
        [InlineData(true, false, 1, BridgeState.Running)]
        [InlineData(true, false, 2, BridgeState.Faulted)]
        [InlineData(true, false, 7, BridgeState.Faulted)]
        [InlineData(true, true, 0, BridgeState.Running)]
        [InlineData(true, true, 2, BridgeState.Running)]
        public void MapsSignalsToTrayStates(bool running, bool authed, int restarts, BridgeState expected)
        {
            Assert.Equal(expected, BridgeHost.DeriveState(running, authed, restarts));
        }

        [Theory]
        [InlineData(false, AdvertisementStatus.Checking, BridgeState.AwaitingPairing)]
        [InlineData(false, AdvertisementStatus.Visible, BridgeState.AwaitingPairing)]
        [InlineData(false, AdvertisementStatus.Missing, BridgeState.Faulted)]
        [InlineData(true, AdvertisementStatus.NotApplicable, BridgeState.Connected)]
        public void MatterStatusDistinguishesPairablePairedAndUndiscoverable(
            bool commissioned,
            AdvertisementStatus advertisement,
            BridgeState expected)
        {
            var status = new MatterStatusFrame(commissioned, advertisement);
            Assert.Equal(expected, BridgeHost.DeriveState(true, true, 0, status));
        }
    }

    public sealed class LifecycleSerialization
    {
        [Fact]
        public async Task RapidOffThenOnRunsInArrivalOrderAndEndsEnabled()
        {
            using var stopEntered = new ManualResetEventSlim();
            using var allowStopToFinish = new ManualResetEventSlim();
            var transitions = new List<bool>();
            var queue = new SerialActionQueue(ex => throw new Xunit.Sdk.XunitException(ex.Message));

            Task off = queue.EnqueueAsync(() =>
            {
                transitions.Add(false);
                stopEntered.Set();
                allowStopToFinish.Wait();
            });
            // Polling wait: returns the instant the queue worker starts, so a
            // generous ceiling is free locally and survives a loaded CI runner
            // under coverage instrumentation (0.4.4: 2 s was too tight there).
            Assert.True(stopEntered.Wait(TimeSpan.FromSeconds(30)));
            Task on = queue.EnqueueAsync(() => transitions.Add(true));

            Assert.False(on.IsCompleted, "enable must wait for the complete stop operation");
            allowStopToFinish.Set();
            await Task.WhenAll(off, on);
            queue.Complete();

            Assert.Equal([false, true], transitions);
            Assert.True(transitions[^1]);
        }
    }

    public sealed class PowerRouting
    {
        [Theory]
        [InlineData(PowerOffAction.DisplaysOff, false)]
        [InlineData(PowerOffAction.PauseAndDisplaysOff, false)]
        [InlineData(PowerOffAction.Screensaver, false)]
        [InlineData(PowerOffAction.Sleep, true)]
        public void MomentaryPolicyMatchesWhetherTheActionTakesTheBridgeOffline(
            PowerOffAction action,
            bool expected)
        {
            Assert.Equal(expected, SidecarEnvironment.IsMomentaryPowerAction(action));
        }

        [Theory]
        [InlineData(PowerOffAction.DisplaysOff, false)]
        [InlineData(PowerOffAction.PauseAndDisplaysOff, false)]
        [InlineData(PowerOffAction.Screensaver, false)]
        [InlineData(PowerOffAction.Sleep, true)]
        public void EndpointsEnvCarriesTheDerivedPowerPolicyIndependentlyOfCustomResetDelay(
            PowerOffAction action,
            bool expected)
        {
            var config = new BridgeConfig
            {
                PowerOffAction = action,
                MomentaryResetMs = 2000,
            };

            Dictionary<string, string> env = SidecarEnvironment.BuildExtraEnv(config);
            using JsonDocument endpoints = JsonDocument.Parse(env["HTPC_BRIDGE_ENDPOINTS"]);

            Assert.Equal(
                expected,
                endpoints.RootElement.GetProperty("power").GetProperty("momentary").GetBoolean());
            Assert.Equal("2000", env["HTPC_BRIDGE_MOMENTARY_RESET_MS"]);
        }

        [Fact]
        public void PowerActionChangeRequiresSidecarRestartSoTheDerivedFlagIsRecomputed()
        {
            var before = new BridgeConfig { PowerOffAction = PowerOffAction.Screensaver };
            var after = new BridgeConfig { PowerOffAction = PowerOffAction.Sleep };

            Assert.True(BridgeHost.RequiresSidecarRestart(before, after));
        }

        [Theory]
        [InlineData(PowerOffAction.DisplaysOff, false, "powerOff")]
        [InlineData(PowerOffAction.DisplaysOff, true, "powerOn")]
        [InlineData(PowerOffAction.PauseAndDisplaysOff, false, "pause,powerOff")]
        [InlineData(PowerOffAction.PauseAndDisplaysOff, true, "powerOn")]
        [InlineData(PowerOffAction.Screensaver, false, "startScreenSaver")]
        [InlineData(PowerOffAction.Screensaver, true, "stopScreenSaver")]
        [InlineData(PowerOffAction.Sleep, false, "sleep")]
        [InlineData(PowerOffAction.Sleep, true, "")]
        public void ConfiguredActionSelectsTheConcretePrimitiveInBothToggleDirections(
            PowerOffAction action,
            bool on,
            string expectedCalls)
        {
            var calls = new List<string>();

            (bool ok, _) = BridgeActionDispatcher.RoutePowerAction(
                action,
                on,
                (name, _) =>
                {
                    calls.Add(name);
                    return true;
                });

            Assert.True(ok);
            Assert.Equal(expectedCalls.Split(',', StringSplitOptions.RemoveEmptyEntries), calls);
        }

        [Fact]
        public void PauseFailureStillTurnsDisplaysOffAndFailsTheAction()
        {
            var calls = new List<string>();

            (bool ok, _) = BridgeActionDispatcher.RoutePowerAction(
                PowerOffAction.PauseAndDisplaysOff,
                on: false,
                (name, _) =>
                {
                    calls.Add(name);
                    return name != "pause";
                });

            Assert.False(ok);
            Assert.Equal(["pause", "powerOff"], calls);
        }

        [Theory]
        [InlineData(PowerOffAction.DisplaysOff, DisplayPowerOffPath.Ddc, "displays off")]
        [InlineData(PowerOffAction.DisplaysOff, DisplayPowerOffPath.BlankingFallback, "Displays off — standby likely")]
        [InlineData(PowerOffAction.PauseAndDisplaysOff, DisplayPowerOffPath.Ddc, "paused + displays off")]
        [InlineData(PowerOffAction.PauseAndDisplaysOff, DisplayPowerOffPath.BlankingFallback, "Paused + displays off — standby likely")]
        public void DisplaysOffPillReflectsThePathThatActuallyExecuted(
            PowerOffAction action,
            DisplayPowerOffPath path,
            string expectedPill)
        {
            int resultCalls = 0;

            (bool ok, string pill) = BridgeActionDispatcher.RoutePowerAction(
                action,
                on: false,
                (name, _) => name == "pause"
                    ? true
                    : throw new InvalidOperationException("Generic power-off must not hide the path."),
                () =>
                {
                    resultCalls++;
                    return new DisplayPowerOffResult(true, path);
                });

            Assert.True(ok);
            Assert.Equal(expectedPill, pill);
            Assert.Equal(1, resultCalls);
        }

        [Fact]
        public void BridgeDisableAlwaysRequestsKeepAwakeReleaseWithoutStartingNativeActions()
        {
            var executor = new ReleaseRecordingExecutor();
            var config = new Config(
                Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"), "config.json"),
                (_, _) => { });
            using var host = new BridgeHost(
                config,
                executor,
                new SidecarSpec("unused.exe", [], Path.GetTempPath()),
                log: (_, _) => { });

            host.SetEnabled(false);

            Assert.Equal(1, executor.ReleaseCalls);
            Assert.Equal(1, executor.ClearScreensaverFocusCalls);
        }

        [Fact]
        public void AppExitClearsScreensaverFocusCapture()
        {
            var executor = new ReleaseRecordingExecutor();
            var config = new Config(
                Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"), "config.json"),
                (_, _) => { });
            var host = new BridgeHost(
                config,
                executor,
                new SidecarSpec("unused.exe", [], Path.GetTempPath()),
                log: (_, _) => { });

            host.Dispose();

            Assert.Equal(1, executor.ClearScreensaverFocusCalls);
        }

        [Theory]
        [InlineData(PowerOffAction.DisplaysOff)]
        [InlineData(PowerOffAction.PauseAndDisplaysOff)]
        [InlineData(PowerOffAction.Screensaver)]
        [InlineData(PowerOffAction.Sleep)]
        public void EveryPowerOnRouteUnconditionallyReleasesAnyDisplayHold(PowerOffAction action)
        {
            var executor = new ReleaseRecordingExecutor();
            var config = new Config(
                Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"), "config.json"),
                (_, _) => { });
            config.Current.PowerOffAction = action;
            using var host = new BridgeHost(
                config,
                executor,
                new SidecarSpec("unused.exe", [], Path.GetTempPath()),
                log: (_, _) => { });

            (bool ok, _, _) = host.ExecutePowerAction(on: true);

            Assert.True(ok);
            Assert.Equal(1, executor.ReleaseCalls);
        }

        [Fact]
        public void ConfigChangeAwayFromDisplayModeReleasesAnyHeldGuard()
        {
            string path = Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"), "config.json");
            var executor = new ReleaseRecordingExecutor();
            var config = new Config(path, (_, _) => { });
            using var host = new BridgeHost(
                config,
                executor,
                new SidecarSpec("unused.exe", [], Path.GetTempPath()),
                log: (_, _) => { });
            var external = new Config(path, (_, _) => { });
            external.Current.PowerOffAction = PowerOffAction.Screensaver;
            Assert.True(external.Save());

            config.Reload();

            Assert.Equal(1, executor.ReleaseCalls);
        }

        [Fact]
        public void ConfigChangeReconcilesRetainedMouseCapturesAgainstEnabledMouseCommands()
        {
            string path = Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"), "config.json");
            var executor = new ReleaseRecordingExecutor();
            var config = new Config(path, (_, _) => { });
            config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "park-mouse",
                    Name = "Park Mouse",
                    Action = new MouseMoveActionConfig { Target = MouseTarget.BottomRight },
                },
            ];
            Assert.True(config.Save());
            using var host = new BridgeHost(
                config,
                executor,
                new SidecarSpec("unused.exe", [], Path.GetTempPath()),
                log: (_, _) => { });
            var external = new Config(path, (_, _) => { });
            external.Current.Commands.Custom[0].Enabled = false;
            Assert.True(external.Save());

            config.Reload();

            Assert.Equal(["park-mouse"], executor.MouseReconciliations[0]);
            Assert.Empty(executor.MouseReconciliations[1]);
        }

        [Fact]
        public void PowerRoutingUsesTheCapturedSessionPolicyWhenLiveConfigChanges()
        {
            var executor = new ReleaseRecordingExecutor();
            var config = new Config(
                Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"), "config.json"),
                (_, _) => { });
            config.Current.PowerOffAction = PowerOffAction.Screensaver;
            using var host = new BridgeHost(
                config,
                executor,
                new SidecarSpec("unused.exe", [], Path.GetTempPath()),
                log: (_, _) => { });

            config.Current.PowerOffAction = PowerOffAction.Sleep;
            (bool ok, string pill, _) = host.ExecutePowerAction(on: false);

            Assert.True(ok);
            Assert.Equal("screensaver started", pill);
            Assert.Contains(("startScreenSaver", (object?)null), executor.Calls);
            Assert.DoesNotContain(("sleep", (object?)null), executor.Calls);
        }

        [Fact]
        public void AuthenticatedClientLossAndSupervisorRestartEachReleaseAnyHeldGuard()
        {
            var executor = new ReleaseRecordingExecutor();
            var config = new Config(
                Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"), "config.json"),
                (_, _) => { });
            using var host = new BridgeHost(
                config,
                executor,
                new SidecarSpec("unused.exe", [], Path.GetTempPath()),
                log: (_, _) => { });

            host.HandleAuthenticatedClientChanged(connected: false);
            host.HandleSupervisorRestartScheduled();

            Assert.Equal(2, executor.ReleaseCalls);
        }

        private sealed class ReleaseRecordingExecutor : IActionExecutor
        {
            private readonly List<(string Name, object? Value)> _calls = [];

            public event EventHandler<VolumeState>? VolumeChanged
            {
                add { }
                remove { }
            }

            public int ReleaseCalls { get; private set; }

            public int ClearScreensaverFocusCalls { get; private set; }

            public List<HashSet<string>> MouseReconciliations { get; } = [];

            public IReadOnlyList<(string Name, object? Value)> Calls => _calls;

            public bool Execute(string name, object? value = null)
            {
                _calls.Add((name, value));
                return true;
            }

            public bool ReleaseDisplayKeepAwake()
            {
                ReleaseCalls++;
                return true;
            }

            public void ReconcileMouseMoves(IReadOnlySet<string> activeCommandKeys) =>
                MouseReconciliations.Add([.. activeCommandKeys]);

            public void ClearScreensaverFocusCapture() => ClearScreensaverFocusCalls++;

            public VolumeState GetVolumeState() => new(50, false);

            public void Dispose()
            {
            }
        }
    }

    public sealed class CustomActionRouting
    {
        [Theory]
        [InlineData(true, true)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void MouseMoveDispatcherCarriesCommandKeyConfigAndEdgeToExecutor(bool on, bool executorResult)
        {
            var action = new MouseMoveActionConfig { Target = MouseTarget.Custom, X = -5, Y = 10 };
            string? verb = null;
            object? payload = null;

            (bool ok, string pill, string? error) = BridgeActionDispatcher.RouteMouseMoveAction(
                "park-mouse",
                action,
                on,
                (name, value) =>
                {
                    verb = name;
                    payload = value;
                    return executorResult;
                });

            Assert.Equal(executorResult, ok);
            Assert.Equal(executorResult ? on ? "mouse moved" : "mouse restored" : "failed", pill);
            Assert.Equal("mouseMove", verb);
            MouseMoveRequest request = Assert.IsType<MouseMoveRequest>(payload);
            Assert.Equal("park-mouse", request.CommandKey);
            Assert.Same(action, request.Action);
            Assert.Equal(on, request.On);
            Assert.Equal(executorResult, error is null);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SequenceMouseDispatcherCarriesOnlyAStatelessOneShotRequest(bool executorResult)
        {
            var action = new MouseMoveActionConfig { Target = MouseTarget.Custom, X = -5, Y = 10 };
            string? verb = null;
            object? payload = null;

            (bool ok, string pill, string? error) = BridgeActionDispatcher.RouteMouseMoveStep(
                "movie-time",
                action,
                (name, value) =>
                {
                    verb = name;
                    payload = value;
                    return executorResult;
                });

            Assert.Equal(executorResult, ok);
            Assert.Equal(executorResult ? "mouse moved" : "failed", pill);
            Assert.Equal("mouseMoveOnce", verb);
            MouseMoveOnceRequest request = Assert.IsType<MouseMoveOnceRequest>(payload);
            Assert.Same(action, request.Action);
            Assert.Equal(executorResult, error is null);
        }

        [Fact]
        public void SequenceRunnerExecutesAOneShotMouseStepThenContinuesInOrder()
        {
            var sequence = new SequenceActionConfig
            {
                Steps =
                [
                    new MouseMoveActionConfig { Target = MouseTarget.TopLeft },
                    new MediaKeyActionConfig { KeyName = MediaKeyName.Stop },
                ],
            };
            var calls = new List<string>();

            (bool ok, string pill, string? error) = BridgeActionDispatcher.RunSequenceSteps(
                "park-then-stop",
                sequence,
                step =>
                {
                    if (step is MouseMoveActionConfig mouseMove)
                    {
                        return BridgeActionDispatcher.RouteMouseMoveStep(
                            "park-then-stop",
                            mouseMove,
                            (name, _) =>
                            {
                                calls.Add(name);
                                return true;
                            });
                    }

                    calls.Add("mediaStop");
                    return (true, "stopped", null);
                });

            Assert.True(ok);
            Assert.Equal("ran 2 steps", pill);
            Assert.Null(error);
            Assert.Equal(["mouseMoveOnce", "mediaStop"], calls);
        }

        [Fact]
        public void SequenceRunnerStopsAtAFailingOneShotMouseStep()
        {
            var sequence = new SequenceActionConfig
            {
                Steps =
                [
                    new MouseMoveActionConfig { Target = MouseTarget.BottomRight },
                    new MediaKeyActionConfig { KeyName = MediaKeyName.Next },
                ],
            };
            int calls = 0;

            (bool ok, string pill, string? error) = BridgeActionDispatcher.RunSequenceSteps(
                "failed-mouse-macro",
                sequence,
                step =>
                {
                    calls++;
                    return step is MouseMoveActionConfig mouseMove
                        ? BridgeActionDispatcher.RouteMouseMoveStep("failed-mouse-macro", mouseMove, (_, _) => false)
                        : (true, "next", null);
                });

            Assert.False(ok);
            Assert.Equal("failed", pill);
            Assert.Contains("step 1 of 2", error, StringComparison.Ordinal);
            Assert.Equal(1, calls);
        }

    }

    /// <summary>
    /// The S3-1 packaged-layout selection
    /// (<see cref="SidecarLaunchSpec.TryPackaged"/>): the Node SEA exe
    /// (<c>sidecar\bridge.exe</c>, no args) wins whenever present; the
    /// ADR-007 §2 fallback layout (<c>sidecar\node.exe</c> +
    /// <c>sidecar\bridge.cjs</c>) is honored otherwise; no packaged layout
    /// means null (Default() then walks the dev fallback chain).
    /// </summary>
    public sealed class PackagedLayout : IDisposable
    {
        private readonly string _baseDir;
        private readonly string _sidecarDir;

        public PackagedLayout()
        {
            _baseDir = Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"));
            _sidecarDir = Path.Combine(_baseDir, "sidecar");
            Directory.CreateDirectory(_sidecarDir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_baseDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }

        private void Touch(string fileName)
        {
            File.WriteAllText(Path.Combine(_sidecarDir, fileName), "stub");
        }

        [Fact]
        public void SeaExeIsSelectedWithNoArgs()
        {
            Touch("bridge.exe");

            SidecarSpec? spec = SidecarLaunchSpec.TryPackaged(_baseDir);

            Assert.NotNull(spec);
            Assert.Equal(Path.Combine(_sidecarDir, "bridge.exe"), spec.ExePath);
            Assert.Empty(spec.Args);
            Assert.Equal(_sidecarDir, spec.WorkingDirectory);
        }

        [Fact]
        public void SeaExeWinsOverNodeBesideBundle()
        {
            Touch("bridge.exe");
            Touch("node.exe");
            Touch("bridge.cjs");

            SidecarSpec? spec = SidecarLaunchSpec.TryPackaged(_baseDir);

            Assert.NotNull(spec);
            Assert.Equal(Path.Combine(_sidecarDir, "bridge.exe"), spec.ExePath);
            Assert.Empty(spec.Args);
        }

        [Fact]
        public void NodeManifestWinsOverAStaleSeaExecutable()
        {
            Touch("bridge.exe");
            Touch("node.exe");
            Touch("bridge.cjs");
            File.WriteAllText(
                Path.Combine(_baseDir, "sidecar-layout.json"),
                """{"layout":"node","files":["sidecar/node.exe","sidecar/bridge.cjs"]}""");

            SidecarSpec? spec = SidecarLaunchSpec.TryPackaged(_baseDir);

            Assert.NotNull(spec);
            Assert.Equal(Path.Combine(_sidecarDir, "node.exe"), spec.ExePath);
            Assert.Equal([Path.Combine(_sidecarDir, "bridge.cjs")], spec.Args);
        }

        [Fact]
        public void SeaManifestWinsOverAStaleNodeLayout()
        {
            Touch("bridge.exe");
            Touch("node.exe");
            Touch("bridge.cjs");
            File.WriteAllText(
                Path.Combine(_baseDir, "sidecar-layout.json"),
                """{"layout":"sea","files":["sidecar/bridge.exe"]}""");

            SidecarSpec? spec = SidecarLaunchSpec.TryPackaged(_baseDir);

            Assert.NotNull(spec);
            Assert.Equal(Path.Combine(_sidecarDir, "bridge.exe"), spec.ExePath);
            Assert.Empty(spec.Args);
        }

        [Fact]
        public void NodeBesideBundleIsTheFallbackLayout()
        {
            Touch("node.exe");
            Touch("bridge.cjs");

            SidecarSpec? spec = SidecarLaunchSpec.TryPackaged(_baseDir);

            Assert.NotNull(spec);
            Assert.Equal(Path.Combine(_sidecarDir, "node.exe"), spec.ExePath);
            Assert.Equal([Path.Combine(_sidecarDir, "bridge.cjs")], spec.Args);
            Assert.Equal(_sidecarDir, spec.WorkingDirectory);
        }

        [Fact]
        public void NodeWithoutBundleIsNotAPackagedLayout()
        {
            Touch("node.exe");

            Assert.Null(SidecarLaunchSpec.TryPackaged(_baseDir));
        }

        [Fact]
        public void EmptySidecarDirIsNotAPackagedLayout()
        {
            Assert.Null(SidecarLaunchSpec.TryPackaged(_baseDir));
        }
    }

    /// <summary>
    /// The S6-1 dev-bundle staleness rule (<see cref="SidecarLaunchSpec.IsBundleFresh"/>):
    /// a bundle only wins over tsx when it exists and no file under
    /// <c>bridge/src</c> (recursively) is newer — stale bundles must lose so
    /// a dev loop that skips <c>npm run bundle</c> never runs old code.
    /// </summary>
    public sealed class BundleFreshness : IDisposable
    {
        private readonly string _dir;
        private readonly string _srcDir;
        private readonly string _bundlePath;

        public BundleFreshness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"));
            _srcDir = Path.Combine(_dir, "src");
            _bundlePath = Path.Combine(_dir, "dist", "bridge.cjs");
            Directory.CreateDirectory(Path.Combine(_srcDir, "ipc"));
            Directory.CreateDirectory(Path.Combine(_dir, "dist"));
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

        private static void WriteWithMtime(string path, DateTime mtimeUtc)
        {
            File.WriteAllText(path, "// content");
            File.SetLastWriteTimeUtc(path, mtimeUtc);
        }

        [Fact]
        public void MissingBundleIsStale()
        {
            WriteWithMtime(Path.Combine(_srcDir, "index.ts"), DateTime.UtcNow.AddHours(-2));

            Assert.False(SidecarLaunchSpec.IsBundleFresh(_bundlePath, _srcDir));
        }

        [Fact]
        public void BundleNewerThanEverySourceFileIsFresh()
        {
            WriteWithMtime(Path.Combine(_srcDir, "index.ts"), DateTime.UtcNow.AddHours(-2));
            WriteWithMtime(Path.Combine(_srcDir, "ipc", "client.ts"), DateTime.UtcNow.AddHours(-3));
            WriteWithMtime(_bundlePath, DateTime.UtcNow.AddHours(-1));

            Assert.True(SidecarLaunchSpec.IsBundleFresh(_bundlePath, _srcDir));
        }

        [Fact]
        public void ANewerNestedSourceFileMakesTheBundleStale()
        {
            WriteWithMtime(Path.Combine(_srcDir, "index.ts"), DateTime.UtcNow.AddHours(-3));
            WriteWithMtime(_bundlePath, DateTime.UtcNow.AddHours(-2));
            WriteWithMtime(Path.Combine(_srcDir, "ipc", "client.ts"), DateTime.UtcNow.AddHours(-1));

            Assert.False(SidecarLaunchSpec.IsBundleFresh(_bundlePath, _srcDir));
        }

        [Fact]
        public void AMissingSourceDirLeavesAnExistingBundleFresh()
        {
            WriteWithMtime(_bundlePath, DateTime.UtcNow.AddHours(-1));

            Assert.True(SidecarLaunchSpec.IsBundleFresh(_bundlePath, Path.Combine(_dir, "no-such-src")));
        }
    }

    [Collection(IpcListenerSuite.Name)]
    public sealed class Wiring : IDisposable
    {
        private const string ActionId = "123e4567-e89b-12d3-a456-426614174000";

        private static readonly SupervisorOptions _fastOptions = new()
        {
            BackoffBaseMs = 50,
            BackoffCapMs = 400,
            StopGraceMs = 1_000,
        };

        private readonly string _dir;
        private readonly Config _config;
        private readonly TestSupport.LogCapture _log = new();
        private readonly FakeExecutor _executor = new();
        private readonly Lock _gate = new();
        private readonly List<OverlayContent> _overlay = [];
        private readonly List<BridgeState> _states = [];
        private readonly List<PairingFrame> _pairings = [];

        public Wiring()
        {
            _dir = Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _config = new Config(Path.Combine(_dir, "config.json"), _log.Sink);
            _config.Current.IpcPort = TestSupport.GetFreeLoopbackPort();
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

        [Fact]
        public async Task ActionFrameExecutesFlashesOverlayAndAcksOkThenDisableGoesGray()
        {
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"setVolume","value":25}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Contains(("setVolume", (object?)25)),
                TimeSpan.FromSeconds(10),
                "executor to receive setVolume 25");
            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage($$"""recv {"v":5,"type":"ack","id":"{{ActionId}}","ok":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the ok ack");

            lock (_gate)
            {
                // S4-5: a successful setVolume flash carries the level so the
                // HUD renders the percentage bar; the primary line drops the
                // redundant percent (maintainer request) — the bar shows it.
                Assert.Contains(
                    new OverlayContent("Google Home → Volume", "volume set to 25 %", false) { VolumePercent = 25 },
                    _overlay);
                Assert.Contains(BridgeState.Connected, _states);
            }

            host.SetEnabled(false);
            Assert.Equal(BridgeState.Disabled, host.State);
            Assert.Equal(1, _executor.ReleaseDisplayKeepAwakeCalls);
            Assert.True(
                _log.Contains("INFO", "exited after stdin close (tether)"),
                "the stub must exit via the stdin tether on stop");
        }

        [Theory]
        [InlineData("play")]
        [InlineData("pause")]
        public async Task DedicatedTransportActionExecutesTheMatchingAbsoluteVerb(string verb)
        {
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"{{verb}}"}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Contains((verb, (object?)null)),
                TimeSpan.FromSeconds(10),
                $"executor to receive dedicated {verb}");
        }

        [Fact]
        public async Task FailedActionFlashesErrorPillAndNacksWithTheIntent()
        {
            _executor.NextResult = false;
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"setVolume","value":25}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage(
                    $$"""recv {"v":5,"type":"ack","id":"{{ActionId}}","ok":false,"error":"action failed: volume 25 %"}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the fail ack with the intent text");

            lock (_gate)
            {
                // A failed volume action degrades to the plain error pill (no bar).
                Assert.Contains(new OverlayContent("Google Home → volume 25 %", "failed", true), _overlay);
            }
        }

        [Fact]
        public async Task StateFrameIsSentOnConnectAndOnEveryVolumeChange()
        {
            _executor.State = new VolumeState(55, false);
            using var host = CreateHost(NodeClientSpec()); // hello only
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage("""recv {"v":5,"type":"state","volume":55,"muted":false}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the on-connect state snapshot");

            _executor.RaiseVolumeChanged(new VolumeState(61, true));

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage("""recv {"v":5,"type":"state","volume":61,"muted":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the volume-change state frame");

            _executor.RaiseVolumeChanged(new VolumeState(73, false));
            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage("""recv {"v":5,"type":"state","volume":73,"muted":false}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the second volume-change state frame");

            _executor.RaiseVolumeChanged(new VolumeState(12, true));
            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage("""recv {"v":5,"type":"state","volume":12,"muted":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the third volume-change state frame");
        }

        [Fact]
        public async Task VolumeEchoWithinTheDeadBandIsNotPublishedBackToGoogle()
        {
            // Owner bug: Google sets 76 %, Windows snaps to 77 %, and echoing
            // the read-back made the Home app bounce its own slider. The ±1 %
            // read-back right after a command must be swallowed; a genuinely
            // different change must still publish.
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"setVolume","value":25}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage($$"""recv {"v":5,"type":"ack","id":"{{ActionId}}","ok":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the ok ack");

            // The CoreAudio echo for the command, quantized one point up.
            _executor.RaiseVolumeChanged(new VolumeState(26, false));
            // A real change right after — must arrive, and the echo must not.
            _executor.RaiseVolumeChanged(new VolumeState(40, false));

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage("""recv {"v":5,"type":"state","volume":40,"muted":false}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the genuine volume-change state frame");

            // State sends are serialized, so observing 40 proves any wrongly
            // published earlier 26 frame would already be in the log. The
            // fake-time publisher unit test pins the quiet-gap boundary.
            Assert.False(
                _log.ContainsMessage("""recv {"v":5,"type":"state","volume":26,"muted":false}"""),
                "the ±1 echo of the commanded volume must be suppressed");
        }

        [Fact]
        public async Task PairingFrameSurfacesWithPayloadAndFlashesTheOverlay()
        {
            using var host = CreateHost(NodeClientSpec(
                """{"v":5,"type":"pairing","qrPayload":"MT:TEST","manualCode":"1111-222-3333"}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () =>
                {
                    lock (_gate)
                    {
                        return _pairings.Count > 0;
                    }
                },
                TimeSpan.FromSeconds(10),
                "pairing frame to surface");

            lock (_gate)
            {
                PairingFrame pairing = Assert.Single(_pairings);
                Assert.Equal("MT:TEST", pairing.QrPayload);
                Assert.Equal("1111-222-3333", pairing.ManualCode);
                Assert.Contains(_overlay, c => c.Primary == "Google Home pairing" && !c.IsError);
            }
        }

        [Fact]
        public async Task MissingAdvertisementProducesErrorOverlayLogAndFaultedTrayState()
        {
            using var host = CreateHost(NodeClientSpec(
                """{"v":5,"type":"matterStatus","commissioned":false,"advertisement":"missing"}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => host.State == BridgeState.Faulted,
                TimeSpan.FromSeconds(10),
                "missing advertisement to fault the tray state");

            Assert.True(_log.Contains("ERROR", "mDNS advertisement is not observable"));
            lock (_gate)
            {
                Assert.Contains(_overlay, content => content.IsError && content.Primary == "Google Home pairing");
            }
        }

        [Fact]
        public async Task CustomMediaKeyActionDispatchesResolvesTheDisplayNameAndAcksOk()
        {
            _config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "stop-media",
                    Name = "HTPC Stop",
                    Action = new MediaKeyActionConfig { KeyName = MediaKeyName.Stop },
                },
            ];
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"custom","key":"stop-media","on":true}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Contains(("mediaStop", (object?)null)),
                TimeSpan.FromSeconds(10),
                "executor to receive mediaStop");
            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage($$"""recv {"v":5,"type":"ack","id":"{{ActionId}}","ok":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the ok ack");

            lock (_gate)
            {
                // ADR-004: the overlay flashes the command's display name.
                Assert.Contains(new OverlayContent("Google Home → HTPC Stop", "stop pressed", false), _overlay);
            }
        }

        [Theory]
        [InlineData(MediaKeyName.Play, "mediaPlay")]
        [InlineData(MediaKeyName.Pause, "mediaPause")]
        public async Task DedicatedPlayAndPauseDispatchTheirAbsoluteExecutorVerbs(MediaKeyName keyName, string verb)
        {
            // S9-1: absolute play/pause (appcommand channel), distinct from
            // the PlayPause toggle key.
            _config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "abs-key",
                    Name = "Abs Key",
                    Action = new MediaKeyActionConfig { KeyName = keyName },
                },
            ];
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"custom","key":"abs-key","on":true}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Contains((verb, (object?)null)),
                TimeSpan.FromSeconds(10),
                $"executor to receive {verb}");
        }

        [Theory]
        [InlineData(MediaKeyName.VolumeUp, 5)]
        [InlineData(MediaKeyName.VolumeDown, -5)]
        public async Task CustomVolumeStepActionsMapToPlusMinusFivePercent(MediaKeyName keyName, int expectedDelta)
        {
            _config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "volume-nudge",
                    Name = "Volume Nudge",
                    Action = new MediaKeyActionConfig { KeyName = keyName },
                },
            ];
            _executor.State = new VolumeState(45, false);
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"custom","key":"volume-nudge","on":true}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Contains(("volumeStep", (object?)expectedDelta)),
                TimeSpan.FromSeconds(10),
                $"executor to receive volumeStep {expectedDelta}");

            // S4-5: the flash carries the resulting level read back from the
            // executor after the step (the fake's state stays at 45).
            await TestSupport.WaitUntilAsync(
                () =>
                {
                    lock (_gate)
                    {
                        return _overlay.Any(c => c is { VolumePercent: 45, Muted: false, IsError: false });
                    }
                },
                TimeSpan.FromSeconds(10),
                "overlay content to carry the read-back volume level 45");
        }

        [Fact]
        public async Task SetMutedOverlayCarriesTheCurrentLevelWithTheMutedFlag()
        {
            _executor.State = new VolumeState(42, false);
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"setMuted","value":true}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage($$"""recv {"v":5,"type":"ack","id":"{{ActionId}}","ok":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the ok ack");

            lock (_gate)
            {
                // S4-5: mute keeps the bar at the current level, dimmed.
                Assert.Contains(
                    new OverlayContent("Google Home → mute", "muted", false) { VolumePercent = 42, Muted = true },
                    _overlay);
            }
        }

        [Fact]
        public async Task CustomLaunchActionPassesPathAndArgsThroughTheExecutorSeam()
        {
            _config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "movie-mode",
                    Name = "Movie Mode",
                    Action = new LaunchActionConfig { Path = @"C:\apps\kodi.exe", Args = "-fs" },
                },
            ];
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"custom","key":"movie-mode","on":true}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Contains(("launch", (object?)new LaunchRequest(@"C:\apps\kodi.exe", "-fs"))),
                TimeSpan.FromSeconds(10),
                "executor to receive the launch request");
            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage($$"""recv {"v":5,"type":"ack","id":"{{ActionId}}","ok":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the ok ack");

            lock (_gate)
            {
                Assert.Contains(new OverlayContent("Google Home → Movie Mode", "launched kodi.exe", false), _overlay);
            }
        }

        [Fact]
        public async Task CustomKeySequenceActionDispatchesTheParsedChordAndAcksOk()
        {
            _config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "paste-plain",
                    Name = "Paste Plain",
                    Action = new KeySequenceActionConfig { Sequence = "Ctrl+Shift+V" },
                },
            ];
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"custom","key":"paste-plain","on":true}"""));
            host.SetEnabled(true);

            // The executor seam receives the PARSED chord (records compare by
            // value), so no real SendInput ever happens in this test.
            var expectedChord = new ParsedKeyChord(
                KeyChordModifiers.Ctrl | KeyChordModifiers.Shift, KeyChord.Keys["V"]);
            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Contains(("keySequence", (object?)expectedChord)),
                TimeSpan.FromSeconds(10),
                "executor to receive the parsed Ctrl+Shift+V chord");
            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage($$"""recv {"v":5,"type":"ack","id":"{{ActionId}}","ok":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the ok ack");

            lock (_gate)
            {
                // ADR-004: the overlay flashes the command's display name.
                Assert.Contains(new OverlayContent("Google Home → Paste Plain", "Ctrl+Shift+V sent", false), _overlay);
            }
        }

        [Fact]
        public async Task CustomMouseMoveActionDispatchesKeyTargetAndProtocolEdge()
        {
            _config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "park-mouse",
                    Name = "Park Mouse",
                    Action = new MouseMoveActionConfig { Target = MouseTarget.BottomRight },
                },
            ];
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"custom","key":"park-mouse","on":true}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Any(call => call.Name == "mouseMove"),
                TimeSpan.FromSeconds(10),
                "executor to receive the mouseMove request");
            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage($$"""recv {"v":5,"type":"ack","id":"{{ActionId}}","ok":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the ok ack");

            (string name, object? value) = Assert.Single(_executor.Calls);
            Assert.Equal("mouseMove", name);
            MouseMoveRequest request = Assert.IsType<MouseMoveRequest>(value);
            Assert.Equal("park-mouse", request.CommandKey);
            Assert.Equal(MouseTarget.BottomRight, request.Action.Target);
            Assert.True(request.On);
        }

        [Fact]
        public async Task CustomMouseProtocolPreservesOffBeforeOnRepeatedOnAndMultipleCommandEdges()
        {
            _config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "park-mouse",
                    Name = "Park Mouse",
                    Action = new MouseMoveActionConfig { Target = MouseTarget.BottomRight },
                },
                new CustomCommandConfig
                {
                    Key = "park-left",
                    Name = "Park Left",
                    Action = new MouseMoveActionConfig { Target = MouseTarget.BottomLeft },
                },
            ];
            string[] frames =
            [
                "{\"v\":5,\"type\":\"action\",\"id\":\"00000000-0000-4000-8000-000000000001\",\"name\":\"custom\",\"key\":\"park-mouse\",\"on\":false}",
                "{\"v\":5,\"type\":\"action\",\"id\":\"00000000-0000-4000-8000-000000000002\",\"name\":\"custom\",\"key\":\"park-mouse\",\"on\":true}",
                "{\"v\":5,\"type\":\"action\",\"id\":\"00000000-0000-4000-8000-000000000003\",\"name\":\"custom\",\"key\":\"park-mouse\",\"on\":true}",
                "{\"v\":5,\"type\":\"action\",\"id\":\"00000000-0000-4000-8000-000000000004\",\"name\":\"custom\",\"key\":\"park-left\",\"on\":true}",
                "{\"v\":5,\"type\":\"action\",\"id\":\"00000000-0000-4000-8000-000000000005\",\"name\":\"custom\",\"key\":\"park-left\",\"on\":false}",
            ];
            using var host = CreateHost(NodeClientSpec(frames));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Count == frames.Length,
                TimeSpan.FromSeconds(10),
                "executor to receive every custom mouse edge");

            MouseMoveRequest[] requests = _executor.Calls
                .Select(call => Assert.IsType<MouseMoveRequest>(call.Value))
                .ToArray();
            Assert.Equal(
                [false, true, true, true, false],
                requests.Select(request => request.On));
            Assert.Equal(
                ["park-mouse", "park-mouse", "park-mouse", "park-left", "park-left"],
                requests.Select(request => request.CommandKey));
        }

        [Fact]
        public async Task CustomMouseOffEdgeIsPreservedAcrossSidecarRestart()
        {
            _config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "park-mouse",
                    Name = "Park Mouse",
                    Action = new MouseMoveActionConfig { Target = MouseTarget.BottomRight },
                },
            ];
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"custom","key":"park-mouse","on":false}"""));

            host.SetEnabled(true);
            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Count == 1,
                TimeSpan.FromSeconds(10),
                "first sidecar session edge");
            host.SetEnabled(false);
            host.SetEnabled(true);
            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Count == 2,
                TimeSpan.FromSeconds(10),
                "restarted sidecar session edge");

            Assert.All(
                _executor.Calls,
                call => Assert.False(Assert.IsType<MouseMoveRequest>(call.Value).On));
        }

        [Fact]
        public async Task CustomSystemActionDispatchesItsExecutorVerbAndAcksOk()
        {
            // S8-5: lock is the safe representative (the fake executor
            // records the verb; no real side effect in tests).
            _config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "lock-pc",
                    Name = "Lock PC",
                    Action = new SystemActionConfig { Command = SystemCommandName.Lock },
                },
            ];
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"custom","key":"lock-pc","on":true}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Contains(("lock", (object?)null)),
                TimeSpan.FromSeconds(10),
                "executor to receive the lock verb");
            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage($$"""recv {"v":5,"type":"ack","id":"{{ActionId}}","ok":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the ok ack");

            lock (_gate)
            {
                Assert.Contains(new OverlayContent("Google Home → Lock PC", "workstation locked", false), _overlay);
            }
        }

        [Fact]
        public async Task CustomSequenceActionRunsItsStepsInOrderAndAcksOk()
        {
            // S8-3 macro: stop -> wait 1 ms -> chord, one endpoint fire. The
            // delay step routes it through the S8-6 background runner: the
            // ack reports "started", the completion overlay the outcome.
            _config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "movie-time",
                    Name = "Movie Time",
                    Action = new SequenceActionConfig
                    {
                        Steps =
                        [
                            new MediaKeyActionConfig { KeyName = MediaKeyName.Stop },
                            new DelayActionConfig { Ms = 1 },
                            new KeySequenceActionConfig { Sequence = "Ctrl+Shift+V" },
                        ],
                    },
                },
            ];
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"custom","key":"movie-time","on":true}"""));
            host.SetEnabled(true);

            var expectedChord = new ParsedKeyChord(
                KeyChordModifiers.Ctrl | KeyChordModifiers.Shift, KeyChord.Keys["V"]);
            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Contains(("keySequence", (object?)expectedChord)),
                TimeSpan.FromSeconds(10),
                "executor to receive the macro's final chord step");
            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage($$"""recv {"v":5,"type":"ack","id":"{{ActionId}}","ok":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the ok (started) ack");

            // Both executor steps ran, in configured order (the delay step
            // never touches the executor).
            Assert.Equal(
                [("mediaStop", null), ("keySequence", expectedChord)],
                _executor.Calls.Where(c => c.Name is "mediaStop" or "keySequence"));

            // Start overlay first, completion overlay when the runner finishes.
            await TestSupport.WaitUntilAsync(
                () =>
                {
                    lock (_gate)
                    {
                        return _overlay.Contains(new OverlayContent("Google Home → Movie Time", "ran 3 steps", false));
                    }
                },
                TimeSpan.FromSeconds(10),
                "the macro-completed overlay");
            lock (_gate)
            {
                Assert.Contains(new OverlayContent("Google Home → Movie Time", "running 3 steps", false), _overlay);
            }
        }

        [Fact]
        public async Task DelayBearingMacroDoesNotBlockTheActionPipeline()
        {
            // S8-6 regression: the receive loop is the WebSocket read loop —
            // pre-fix, this macro's 3 s wait stalled the volume frame queued
            // right behind it (and its ack) for the full 3 s.
            _config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "slow-macro",
                    Name = "Slow Macro",
                    Action = new SequenceActionConfig
                    {
                        Steps =
                        [
                            new DelayActionConfig { Ms = 3000 },
                            new MediaKeyActionConfig { KeyName = MediaKeyName.Stop },
                        ],
                    },
                },
            ];
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"custom","key":"slow-macro","on":true}""",
                """{"v":5,"type":"action","id":"7f9be2e6-9d0a-4f7e-9a76-1a2b3c4d5e70","name":"setVolume","value":25}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Contains(("setVolume", (object?)25)),
                TimeSpan.FromSeconds(10),
                "the volume frame queued behind the macro to execute");

            // The volume executed while the macro is still inside its 3 s
            // delay — its media-key step must not have run yet.
            Assert.DoesNotContain(("mediaStop", (object?)null), _executor.Calls);
        }

        [Fact]
        public async Task DisposingCancelsAndDrainsTrackedDelayBearingMacros()
        {
            _config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "shutdown-macro",
                    Name = "Shutdown Macro",
                    Action = new SequenceActionConfig
                    {
                        Steps =
                        [
                            new DelayActionConfig { Ms = 3_000 },
                            new MediaKeyActionConfig { KeyName = MediaKeyName.Stop },
                        ],
                    },
                },
            ];
            var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"custom","key":"shutdown-macro","on":true}"""));
            host.SetEnabled(true);
            await TestSupport.WaitUntilAsync(
                () => host.RunningMacroCount == 1,
                TimeSpan.FromSeconds(10),
                "the delay-bearing macro to be tracked");

            host.Dispose();

            Assert.Equal(0, host.RunningMacroCount);
            Assert.DoesNotContain(("mediaStop", (object?)null), _executor.Calls);
            Assert.True(_log.Contains("ERROR", "macro cancelled (bridge session stopped)"));
        }

        [Fact]
        public async Task DelayedMacroCannotCaptureScreensaverFocusAfterDisableReturns()
        {
            _config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "late-screensaver",
                    Name = "Late Screensaver",
                    Action = new SequenceActionConfig
                    {
                        Steps =
                        [
                            new DelayActionConfig { Ms = 3_000 },
                            new SystemActionConfig { Command = SystemCommandName.StartScreenSaver },
                        ],
                    },
                },
            ];
            using var host = CreateHost(NodeClientSpec());
            host.BeginMacroSession();
            (bool started, _, string? error) = host.ExecuteFrame(
                new CustomActionFrame(Guid.Parse(ActionId), "late-screensaver", On: true));
            Assert.True(started, error);
            await TestSupport.WaitUntilAsync(
                () => host.RunningMacroCount == 1,
                TimeSpan.FromSeconds(10),
                "the delayed screensaver macro to enter its wait");

            await host.QueueSetEnabledAsync(false);

            Assert.Equal(0, host.RunningMacroCount);
            Assert.DoesNotContain(("startScreenSaver", (object?)null), _executor.Calls);
            Assert.False(_executor.HasScreensaverFocusCapture);
        }

        [Fact]
        public async Task ScreensaverFocusCaptureCannotSurviveADisableEnableCycle()
        {
            using var host = CreateHost(NodeClientSpec());
            host.BeginMacroSession();
            Assert.True(_executor.Execute("startScreenSaver"));
            Assert.True(_executor.HasScreensaverFocusCapture);

            await host.QueueSetEnabledAsync(false);
            host.BeginMacroSession();

            Assert.False(_executor.HasScreensaverFocusCapture);
        }

        [Fact]
        public async Task ReenabledBridgeRunsMacrosWithAFreshCancellationLifetime()
        {
            _config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "fresh-session",
                    Name = "Fresh Session",
                    Action = new SequenceActionConfig
                    {
                        Steps =
                        [
                            new DelayActionConfig { Ms = 500 },
                            new MediaKeyActionConfig { KeyName = MediaKeyName.Stop },
                        ],
                    },
                },
            ];
            using var host = CreateHost(NodeClientSpec());
            host.BeginMacroSession();
            (bool firstStarted, _, string? firstError) = host.ExecuteFrame(
                new CustomActionFrame(Guid.Parse(ActionId), "fresh-session", On: true));
            Assert.True(firstStarted, firstError);
            await TestSupport.WaitUntilAsync(
                () => host.RunningMacroCount == 1,
                TimeSpan.FromSeconds(10),
                "the first-session macro to enter its wait");
            await host.QueueSetEnabledAsync(false);
            Assert.DoesNotContain(("mediaStop", (object?)null), _executor.Calls);

            host.BeginMacroSession();
            (bool secondStarted, _, string? secondError) = host.ExecuteFrame(
                new CustomActionFrame(Guid.Parse(ActionId), "fresh-session", On: true));
            Assert.True(secondStarted, secondError);
            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Contains(("mediaStop", (object?)null)),
                TimeSpan.FromSeconds(10),
                "the re-enabled session macro to complete");
        }

        [Fact]
        public void DelayBearingMacroIsSingleFlightPerCustomCommand()
        {
            _config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "single-flight",
                    Name = "Single Flight",
                    Action = new SequenceActionConfig
                    {
                        Steps = [new DelayActionConfig { Ms = 10_000 }],
                    },
                },
            ];
            using var host = CreateHost(NodeClientSpec());
            host.BeginMacroSession();

            (bool firstOk, _, string? firstError) = host.ExecuteFrame(
                new CustomActionFrame(Guid.NewGuid(), "single-flight", On: true));
            (bool secondOk, _, string? secondError) = host.ExecuteFrame(
                new CustomActionFrame(Guid.NewGuid(), "single-flight", On: true));

            Assert.True(firstOk, firstError);
            Assert.False(secondOk);
            Assert.Contains("already running", secondError, StringComparison.Ordinal);
            Assert.Equal(1, host.RunningMacroCount);
        }

        [Fact]
        public void DelayBearingMacroGlobalCapReturnsAClearNackReason()
        {
            _config.Current.Commands.Custom = [.. Enumerable.Range(0, BridgeActionDispatcher.MaxConcurrentMacros + 1)
                .Select(index => new CustomCommandConfig
                {
                    Key = $"macro-{index}",
                    Name = $"Macro {index}",
                    Action = new SequenceActionConfig
                    {
                        Steps = [new DelayActionConfig { Ms = 10_000 }],
                    },
                })];
            using var host = CreateHost(NodeClientSpec());
            host.BeginMacroSession();

            for (int index = 0; index < BridgeActionDispatcher.MaxConcurrentMacros; index++)
            {
                (bool ok, _, string? error) = host.ExecuteFrame(
                    new CustomActionFrame(Guid.NewGuid(), $"macro-{index}", On: true));
                Assert.True(ok, error);
            }

            (bool overCap, _, string? overCapError) = host.ExecuteFrame(
                new CustomActionFrame(Guid.NewGuid(), $"macro-{BridgeActionDispatcher.MaxConcurrentMacros}", On: true));

            Assert.False(overCap);
            Assert.Contains("capacity reached", overCapError, StringComparison.Ordinal);
            Assert.Equal(BridgeActionDispatcher.MaxConcurrentMacros, host.RunningMacroCount);
        }

        [Fact]
        public async Task QueuedRapidDisableEnableEndsConnectedWithoutAPortCollision()
        {
            _config.Current.BridgeEnabled = true;
            Assert.True(_config.Save());
            using var host = CreateHost(NodeClientSpec());
            host.SetEnabled(true);
            await TestSupport.WaitUntilAsync(
                () => host.State == BridgeState.Connected,
                TimeSpan.FromSeconds(10),
                "bridge to connect before rapid lifecycle changes");

            Task disabled = host.QueueSetEnabledAsync(false);
            Task enabled = host.QueueSetEnabledAsync(true);
            await Task.WhenAll(disabled, enabled);
            await TestSupport.WaitUntilAsync(
                () => host.State == BridgeState.Connected,
                TimeSpan.FromSeconds(10),
                "bridge to reconnect after queued off/on");

            Assert.True(_config.Current.BridgeEnabled);
            Assert.False(_log.Contains("ERROR", "cannot listen on port"));
        }

        [Fact]
        public async Task CustomSequenceActionStopsAtTheFirstFailingStepAndNacksWithItsNumber()
        {
            _config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "movie-time",
                    Name = "Movie Time",
                    Action = new SequenceActionConfig
                    {
                        Steps =
                        [
                            new MediaKeyActionConfig { KeyName = MediaKeyName.Stop },
                            new MediaKeyActionConfig { KeyName = MediaKeyName.Next },
                        ],
                    },
                },
            ];
            _executor.NextResult = false; // every executor call fails
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"custom","key":"movie-time","on":true}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage("\"ok\":false") && _log.ContainsMessage("step 1 of 2"),
                TimeSpan.FromSeconds(10),
                "stub to receive the nack naming the failing step");

            // Execution stopped at step 1: the second media key never ran.
            Assert.DoesNotContain(("next", (object?)null), _executor.Calls);
        }

        [Fact]
        public void SequenceMediaFailureCarriesTheExecutorsDiagnosticThroughCustomDispatch()
        {
            const string diagnostic = "target timed out; no same-owner session to fall back to";
            _config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "resume",
                    Name = "Resume",
                    Action = new SequenceActionConfig
                    {
                        Steps = [new MediaKeyActionConfig { KeyName = MediaKeyName.Play }],
                    },
                },
            ];
            _executor.NextDetailedResult = ActionExecutionResult.Failure(diagnostic);
            using var host = CreateHost(NodeClientSpec());

            (bool ok, string pill, string? error) = host.ExecuteFrame(
                new CustomActionFrame(Guid.Parse(ActionId), "resume", On: true));

            Assert.False(ok);
            Assert.Equal("failed", pill);
            Assert.Contains("step 1 of 1 failed", error, StringComparison.Ordinal);
            Assert.Contains(diagnostic, error, StringComparison.Ordinal);
            Assert.Contains(("mediaPlay", (object?)null), _executor.Calls);
            Assert.DoesNotContain("play pressed", error, StringComparison.Ordinal);
        }


        [Fact]
        public async Task SupervisorEnvCarriesTheConfiguredMomentaryResetInterval()
        {
            _config.Current.MomentaryResetMs = 450;
            _config.Current.PowerOffAction = PowerOffAction.Sleep;
            _config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "movie-mode",
                    Name = "Movie Mode",
                    ResetAfterActivation = true,
                    Action = new MediaKeyActionConfig { KeyName = MediaKeyName.Stop },
                },
            ];
            const string script =
                "console.log('MRESET=' + process.env.HTPC_BRIDGE_MOMENTARY_RESET_MS);" +
                "console.log('ENDPOINTS=' + process.env.HTPC_BRIDGE_ENDPOINTS);" +
                "process.stdin.resume();" +
                "process.stdin.on('end', () => process.exit(0));" +
                "setInterval(() => {}, 1000);";
            using var host = CreateHost(
                new SidecarSpec(TestSupport.RequireNodeExe(), ["-e", script], Path.GetTempPath()));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage("MRESET=450"),
                TimeSpan.FromSeconds(10),
                "sidecar env to carry HTPC_BRIDGE_MOMENTARY_RESET_MS=450");
            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage("\"resetAfterActivation\":true"),
                TimeSpan.FromSeconds(10),
                "sidecar endpoint env to carry custom resetAfterActivation=true");
            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage("\"momentary\":true"),
                TimeSpan.FromSeconds(10),
                "sidecar endpoint env to carry power momentary=true independently of the custom delay");
        }

        [Fact]
        public async Task UnknownCustomKeyNacksWithAReasonAndNeverHitsTheExecutor()
        {
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"custom","key":"no-such-key","on":true}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage(
                    $$"""recv {"v":5,"type":"ack","id":"{{ActionId}}","ok":false,"error":"unknown or disabled custom command: no-such-key"}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the fail ack naming the unknown key");

            Assert.Empty(_executor.Calls);
            lock (_gate)
            {
                // No display name exists, so the overlay shows the wire key.
                Assert.Contains(new OverlayContent("Google Home → no-such-key", "failed", true), _overlay);
            }
        }

        [Fact]
        public async Task DisabledCustomCommandNacksLikeAnUnknownKey()
        {
            _config.Current.Commands.Custom =
            [
                new CustomCommandConfig
                {
                    Key = "movie-mode",
                    Name = "Movie Mode",
                    Enabled = false,
                    Action = new MediaKeyActionConfig { KeyName = MediaKeyName.PlayPause },
                },
            ];
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"custom","key":"movie-mode","on":true}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage(
                    $$"""recv {"v":5,"type":"ack","id":"{{ActionId}}","ok":false,"error":"unknown or disabled custom command: movie-mode"}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the fail ack for the disabled command");

            Assert.Empty(_executor.Calls);
        }

        [Fact]
        public async Task SupersededV1FrameIsRejectedAndClosesTheSocket()
        {
            // Version-bump proof at the wiring level (ADR-004 §3): a stub
            // still speaking v1 authenticates (hello is v4 in the spec below)
            // but its v1 action must close the socket, not execute.
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":1,"type":"action","id":"{{ActionId}}","name":"playPause"}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.Contains("WARN", "\"v\" must be the integer 5"),
                TimeSpan.FromSeconds(10),
                "the v1 frame to be rejected with the version reason");

            Assert.Empty(_executor.Calls);
        }

        [Fact]
        public async Task ActionsFeedTheAppMetricsCountersAndEmitTheDebugTimingLine()
        {
            using var counters = new CounterCapture();
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":5,"type":"action","id":"{{ActionId}}","name":"setVolume","value":25}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage($$"""recv {"v":5,"type":"ack","id":"{{ActionId}}","ok":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the ok ack");
            await TestSupport.WaitUntilAsync(
                () => counters.Count("actions_executed_ok") >= 1
                    && counters.Count("acks_sent") >= 1
                    && counters.Count("ipc_client_connects") >= 1,
                TimeSpan.FromSeconds(10),
                "the ok/ack/connect counters to increment");

            // ADR-006 §2: grep-friendly timing line at Debug, keyed by the
            // action id (the cross-process correlation key).
            await TestSupport.WaitUntilAsync(
                () => _log.Snapshot().Any(e => e.Level == "DEBUG"
                    && e.Message.StartsWith($"IPC timing: setVolume id={ActionId} execute=", StringComparison.Ordinal)
                    && e.Message.Contains("ms total=", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10),
                "the Debug IPC timing line for setVolume");

            host.SetEnabled(false);
            await TestSupport.WaitUntilAsync(
                () => counters.Count("ipc_client_disconnects") >= 1,
                TimeSpan.FromSeconds(10),
                "the disconnect counter to increment");
        }

        [Fact]
        public async Task SidecarCrashLoopWithoutAuthenticationTurnsFaulted()
        {
            string node = TestSupport.RequireNodeExe();
            using var host = CreateHost(new SidecarSpec(node, ["-e", "process.exit(1);"], Path.GetTempPath()));
            host.SetEnabled(true);

            // Two consecutive restarts without an authenticated connection = red.
            await TestSupport.WaitUntilAsync(
                () =>
                {
                    lock (_gate)
                    {
                        return _states.Contains(BridgeState.Faulted);
                    }
                },
                TimeSpan.FromSeconds(10),
                "state to reach Faulted");

            lock (_gate)
            {
                Assert.Equal(BridgeState.Running, _states[0]); // amber first, red only after the loop
            }

            Assert.True(_executor.ReleaseDisplayKeepAwakeCalls >= 2);
        }

        [Fact]
        public async Task RestartMarkedConfigChangeAutomaticallyRestartsTheRunningBridge()
        {
            _config.Current.BridgeEnabled = true;
            Assert.True(_config.Save());
            using var host = CreateHost(NodeClientSpec());
            host.SetEnabled(true);
            await TestSupport.WaitUntilAsync(
                () => host.State == BridgeState.Connected,
                TimeSpan.FromSeconds(10),
                "bridge to connect before settings reload");

            var external = new Config(Path.Combine(_dir, "config.json"), _log.Sink);
            external.Current.BridgeName = "Restarted bridge";
            Assert.True(external.Save());
            _config.Reload();

            await TestSupport.WaitUntilAsync(
                () => _log.Snapshot().Count(e => e.Message.Contains("bridge: started", StringComparison.Ordinal)) >= 2,
                TimeSpan.FromSeconds(10),
                "bridge to start a second time after restart-marked settings changed");
            Assert.True(_log.Contains("INFO", "saved settings require a restart"));
            lock (_gate)
            {
                Assert.Contains(_overlay, content => content.Primary == "Settings saved" && !content.IsError);
            }
        }

        [Fact]
        public async Task PowerActionChangeRestartsWithARecomputedMomentaryFlag()
        {
            _config.Current.BridgeEnabled = true;
            _config.Current.PowerOffAction = PowerOffAction.Screensaver;
            Assert.True(_config.Save());
            const string script =
                "const e = JSON.parse(process.env.HTPC_BRIDGE_ENDPOINTS);" +
                "console.log('POWER_MOMENTARY=' + e.power.momentary);" +
                "process.stdin.resume();" +
                "process.stdin.on('end', () => process.exit(0));" +
                "setInterval(() => {}, 1000);";
            using var host = CreateHost(
                new SidecarSpec(TestSupport.RequireNodeExe(), ["-e", script], Path.GetTempPath()));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage("POWER_MOMENTARY=false"),
                TimeSpan.FromSeconds(10),
                "the reversible Power policy to reach the first sidecar");

            var external = new Config(Path.Combine(_dir, "config.json"), _log.Sink);
            external.Current.PowerOffAction = PowerOffAction.Sleep;
            Assert.True(external.Save());
            _config.Reload();

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage("POWER_MOMENTARY=true"),
                TimeSpan.FromSeconds(10),
                "the irreversible Power policy to reach the restarted sidecar");
            Assert.True(_log.Contains("INFO", "saved settings require a restart"));
        }

        [Fact]
        public async Task FactoryResetOnADisabledBridgeDeletesStorageAndBringsTheBridgeUp()
        {
            // S10-8: a reset exists only to re-pair, so it now STARTS a bridge
            // that was off (and persists that) - an uncommissioned node that
            // is not running advertises nothing, and the Home app answers
            // "can't find device". Was S3-2's "just deletes, stays disabled".
            string storageDir = Path.Combine(_dir, "matter");
            Directory.CreateDirectory(storageDir);
            File.WriteAllText(Path.Combine(storageDir, "fabric.json"), "{}");

            using BridgeHost host = CreateHost(NodeClientSpec());

            FactoryResetResult result = host.FactoryReset();

            Assert.True(result.Ok);
            Assert.Null(result.Error);
            Assert.False(Directory.Exists(storageDir), "storage dir must be gone");
            Assert.True(_log.Contains("INFO", "factory reset complete"));
            Assert.True(_config.Current.BridgeEnabled, "the reset must persist the bridge as enabled");

            await TestSupport.WaitUntilAsync(
                () => host.State == BridgeState.Connected,
                TimeSpan.FromSeconds(10),
                "bridge to come up after a reset that started from disabled");
        }

        [Fact]
        public void FactoryResetOnANeverPairedBridgeWithNoStorageDirSucceeds()
        {
            // No storage dir ever created — "already reset"/never-paired must
            // count as success, not a delete failure.
            using BridgeHost host = CreateHost(NodeClientSpec());

            FactoryResetResult result = host.FactoryReset();

            Assert.True(result.Ok);
            Assert.Null(result.Error);
        }

        [Fact]
        public async Task FactoryResetWhileRunningStopsDeletesStorageThenReEnablesTheBridge()
        {
            string storageDir = Path.Combine(_dir, "matter");
            Directory.CreateDirectory(storageDir);
            File.WriteAllText(Path.Combine(storageDir, "fabric.json"), "{}");

            using BridgeHost host = CreateHost(NodeClientSpec()); // hello only; reconnects identically after restart
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => host.State == BridgeState.Connected,
                TimeSpan.FromSeconds(10),
                "bridge to connect before the reset");

            FactoryResetResult result = host.FactoryReset();

            Assert.True(result.Ok);
            Assert.Null(result.Error);
            Assert.False(Directory.Exists(storageDir), "storage dir must be gone");
            lock (_gate)
            {
                Assert.Contains(
                    new OverlayContent("Factory reset complete", "restarting — a new pairing code is coming", false),
                    _overlay);
            }

            // The bridge was running before the reset, so FactoryReset must
            // bring it back up — the same stub script re-authenticates with a
            // fresh identity, exactly as a real sidecar would after losing
            // its persisted fabric (BLUEPRINT §2.5's designed re-pair flow).
            await TestSupport.WaitUntilAsync(
                () => host.State == BridgeState.Connected,
                TimeSpan.FromSeconds(10),
                "bridge to reconnect after the reset");
        }

        [Fact]
        public async Task FactoryResetWithALockedLiveFileFailsStagingAndPreservesStorage()
        {
            string storageDir = Path.Combine(_dir, "matter");
            Directory.CreateDirectory(storageDir);
            string lockedFile = Path.Combine(storageDir, "locked.db");
            File.WriteAllText(lockedFile, "locked");

            _config.Current.BridgeEnabled = true;
            Assert.True(_config.Save());
            using BridgeHost host = CreateHost(NodeClientSpec());
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => host.State == BridgeState.Connected,
                TimeSpan.FromSeconds(10),
                "bridge to connect before the reset");

            FactoryResetResult result;
            using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // Windows cannot rename a directory while a child handle does
                // not share delete access. The honest outcome is a failed
                // staging step with the live storage tree untouched.
                result = host.FactoryReset();
            }

            Assert.False(result.Ok);
            Assert.Contains("stage", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(storageDir), "a failed staging rename must preserve the live storage path");
            Assert.True(File.Exists(lockedFile), "a failed staging rename must preserve the live storage contents");
            Assert.False(_log.Contains("WARN", "residue left at"));

            Assert.True(_config.Current.BridgeEnabled);
            await TestSupport.WaitUntilAsync(
                () => host.State == BridgeState.Connected,
                TimeSpan.FromSeconds(10),
                "the bridge to restore its previous session after the failed reset");
            lock (_gate)
            {
                Assert.Contains(_overlay, c => c.Primary == "Factory reset failed" && c.IsError);
            }
        }

        private BridgeHost CreateHost(SidecarSpec spec)
        {
            var host = new BridgeHost(
                _config,
                _executor,
                spec,
                overlaySink: content =>
                {
                    lock (_gate)
                    {
                        _overlay.Add(content);
                    }
                },
                supervisorOptions: _fastOptions,
                log: _log.Sink,
                storageDir: Path.Combine(_dir, "matter"));
            host.StateChanged += (_, state) =>
            {
                lock (_gate)
                {
                    _states.Add(state);
                }
            };
            host.PairingReceived += (_, pairing) =>
            {
                lock (_gate)
                {
                    _pairings.Add(pairing);
                }
            };
            return host;
        }

        /// <summary>
        /// A stub sidecar as an inline node script: real WebSocket, real env
        /// contract, hello, then <paramref name="framesAfterHello"/> verbatim.
        /// Echoes every inbound frame as a pino line (<c>recv …</c>) so the
        /// injected log capture doubles as the client-side assertion channel,
        /// and exits on stdin EOF (the supervisor tether).
        /// </summary>
        private static SidecarSpec NodeClientSpec(params string[] framesAfterHello)
        {
            string script =
                "const p = process.env.HTPC_BRIDGE_IPC_PORT;" +
                "const t = process.env.HTPC_BRIDGE_IPC_TOKEN;" +
                "const ws = new WebSocket('ws://localhost:' + p + '/');" +
                "ws.addEventListener('message', (e) => console.log(JSON.stringify({ level: 30, msg: 'recv ' + e.data })));" +
                "ws.addEventListener('open', () => {" +
                "ws.send(JSON.stringify({ v: 5, type: 'hello', token: t, protocol: 1 }));" +
                "ws.send(JSON.stringify({ v: 5, type: 'matterStatus', commissioned: true, advertisement: 'notApplicable' }));" +
                string.Concat(framesAfterHello.Select(frame => $"ws.send('{frame}');")) +
                "});" +
                "process.stdin.resume();" +
                "process.stdin.on('end', () => process.exit(0));" +
                "setInterval(() => {}, 1000);";
            return new SidecarSpec(TestSupport.RequireNodeExe(), ["-e", script], Path.GetTempPath());
        }

        /// <summary>
        /// Local <see cref="MeterListener"/> summing <see cref="AppMetrics"/>
        /// counter increments observed during its lifetime. The counters are
        /// process-global, so assertions must be "&gt;=", never exact.
        /// </summary>
        private sealed class CounterCapture : IDisposable
        {
            private readonly MeterListener _listener = new();
            private readonly Lock _gate = new();
            private readonly Dictionary<string, long> _counts = [];

            public CounterCapture()
            {
                _listener.InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == AppMetrics.MeterName && instrument is Counter<long>)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                };
                _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
                {
                    lock (_gate)
                    {
                        _counts[instrument.Name] = _counts.GetValueOrDefault(instrument.Name) + measurement;
                    }
                });
                _listener.Start();
            }

            public long Count(string name)
            {
                lock (_gate)
                {
                    return _counts.GetValueOrDefault(name);
                }
            }

            public void Dispose() => _listener.Dispose();
        }

        /// <summary>Thread-safe no-side-effect <see cref="IActionExecutor"/> with scriptable results and volume state.</summary>
        private sealed class FakeExecutor : IActionExecutor
        {
            private readonly Lock _gate = new();
            private readonly List<(string Name, object? Value)> _calls = [];
            private int _releaseDisplayKeepAwakeCalls;
            private bool _hasScreensaverFocusCapture;

            public event EventHandler<VolumeState>? VolumeChanged;

            public bool NextResult { get; set; } = true;

            public ActionExecutionResult? NextDetailedResult { get; set; }

            public VolumeState State { get; set; } = new(55, false);

            public int ReleaseDisplayKeepAwakeCalls => Volatile.Read(ref _releaseDisplayKeepAwakeCalls);

            public bool HasScreensaverFocusCapture
            {
                get
                {
                    lock (_gate)
                    {
                        return _hasScreensaverFocusCapture;
                    }
                }
            }

            public IReadOnlyList<(string Name, object? Value)> Calls
            {
                get
                {
                    lock (_gate)
                    {
                        return [.. _calls];
                    }
                }
            }

            public bool Execute(string name, object? value = null)
            {
                lock (_gate)
                {
                    _calls.Add((name, value));
                    if (name == "startScreenSaver")
                    {
                        _hasScreensaverFocusCapture = true;
                    }
                    else if (name == "stopScreenSaver")
                    {
                        _hasScreensaverFocusCapture = false;
                    }
                }

                return NextResult;
            }

            public ActionExecutionResult ExecuteDetailed(string name, object? value = null)
            {
                bool ok = Execute(name, value);
                return NextDetailedResult
                    ?? (ok
                        ? ActionExecutionResult.Success
                        : ActionExecutionResult.Failure($"action '{name}' failed (see the app log)"));
            }

            public VolumeState GetVolumeState() => State;

            public bool ReleaseDisplayKeepAwake()
            {
                Interlocked.Increment(ref _releaseDisplayKeepAwakeCalls);
                return true;
            }

            public void ClearScreensaverFocusCapture()
            {
                lock (_gate)
                {
                    _hasScreensaverFocusCapture = false;
                }
            }

            public void RaiseVolumeChanged(VolumeState state) => VolumeChanged?.Invoke(this, state);

            public void Dispose()
            {
            }
        }
    }
}
