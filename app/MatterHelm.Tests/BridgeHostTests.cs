using System.Diagnostics.Metrics;
using MatterHelm.Actions;
using MatterHelm.Diagnostics;
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
        [InlineData(true, true, 0, BridgeState.Connected)]
        [InlineData(true, true, 2, BridgeState.Connected)]
        public void MapsSignalsToTrayStates(bool running, bool authed, int restarts, BridgeState expected)
        {
            Assert.Equal(expected, BridgeHost.DeriveState(running, authed, restarts));
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
                $$"""{"v":2,"type":"action","id":"{{ActionId}}","name":"setVolume","value":25}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Contains(("setVolume", (object?)25)),
                TimeSpan.FromSeconds(10),
                "executor to receive setVolume 25");
            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage($$"""recv {"v":2,"type":"ack","id":"{{ActionId}}","ok":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the ok ack");

            lock (_gate)
            {
                // S4-5: a successful setVolume flash carries the level so the
                // HUD renders the percentage bar; the primary line drops the
                // redundant percent (owner request) — the bar shows it.
                Assert.Contains(
                    new OverlayContent("Google Home → Volume", "volume set to 25 %", false) { VolumePercent = 25 },
                    _overlay);
                Assert.Contains(BridgeState.Connected, _states);
            }

            host.SetEnabled(false);
            Assert.Equal(BridgeState.Disabled, host.State);
            Assert.True(
                _log.Contains("INFO", "exited after stdin close (tether)"),
                "the stub must exit via the stdin tether on stop");
        }

        [Fact]
        public async Task FailedActionFlashesErrorPillAndNacksWithTheIntent()
        {
            _executor.NextResult = false;
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":2,"type":"action","id":"{{ActionId}}","name":"setVolume","value":25}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage(
                    $$"""recv {"v":2,"type":"ack","id":"{{ActionId}}","ok":false,"error":"action failed: volume 25 %"}"""),
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
                () => _log.ContainsMessage("""recv {"v":2,"type":"state","volume":55,"muted":false}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the on-connect state snapshot");

            _executor.RaiseVolumeChanged(new VolumeState(61, true));

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage("""recv {"v":2,"type":"state","volume":61,"muted":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the volume-change state frame");
        }

        [Fact]
        public async Task VolumeEchoWithinTheDeadBandIsNotPublishedBackToGoogle()
        {
            // Owner bug: Google sets 76 %, Windows snaps to 77 %, and echoing
            // the read-back made the Home app bounce its own slider. The ±1 %
            // read-back right after a command must be swallowed; a genuinely
            // different change must still publish.
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":2,"type":"action","id":"{{ActionId}}","name":"setVolume","value":25}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage($$"""recv {"v":2,"type":"ack","id":"{{ActionId}}","ok":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the ok ack");

            // The CoreAudio echo for the command, quantized one point up.
            _executor.RaiseVolumeChanged(new VolumeState(26, false));
            // A real change right after — must arrive, and the echo must not.
            _executor.RaiseVolumeChanged(new VolumeState(40, false));

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage("""recv {"v":2,"type":"state","volume":40,"muted":false}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the genuine volume-change state frame");

            // Publishes ride Task.Run: give a wrongly-published echo a moment
            // to land before asserting it never does.
            await Task.Delay(250);
            Assert.False(
                _log.ContainsMessage("""recv {"v":2,"type":"state","volume":26,"muted":false}"""),
                "the ±1 echo of the commanded volume must be suppressed");
        }

        [Fact]
        public async Task PairingFrameSurfacesWithPayloadAndFlashesTheOverlay()
        {
            using var host = CreateHost(NodeClientSpec(
                """{"v":2,"type":"pairing","qrPayload":"MT:TEST","manualCode":"1111-222-3333"}"""));
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
                $$"""{"v":2,"type":"action","id":"{{ActionId}}","name":"custom","key":"stop-media"}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Contains(("mediaStop", (object?)null)),
                TimeSpan.FromSeconds(10),
                "executor to receive mediaStop");
            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage($$"""recv {"v":2,"type":"ack","id":"{{ActionId}}","ok":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the ok ack");

            lock (_gate)
            {
                // ADR-004: the overlay flashes the command's display name.
                Assert.Contains(new OverlayContent("Google Home → HTPC Stop", "stop pressed", false), _overlay);
            }
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
                $$"""{"v":2,"type":"action","id":"{{ActionId}}","name":"custom","key":"volume-nudge"}"""));
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
                $$"""{"v":2,"type":"action","id":"{{ActionId}}","name":"setMuted","value":true}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage($$"""recv {"v":2,"type":"ack","id":"{{ActionId}}","ok":true}"""),
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
                $$"""{"v":2,"type":"action","id":"{{ActionId}}","name":"custom","key":"movie-mode"}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Contains(("launch", (object?)new LaunchRequest(@"C:\apps\kodi.exe", "-fs"))),
                TimeSpan.FromSeconds(10),
                "executor to receive the launch request");
            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage($$"""recv {"v":2,"type":"ack","id":"{{ActionId}}","ok":true}"""),
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
                $$"""{"v":2,"type":"action","id":"{{ActionId}}","name":"custom","key":"paste-plain"}"""));
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
                () => _log.ContainsMessage($$"""recv {"v":2,"type":"ack","id":"{{ActionId}}","ok":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the ok ack");

            lock (_gate)
            {
                // ADR-004: the overlay flashes the command's display name.
                Assert.Contains(new OverlayContent("Google Home → Paste Plain", "Ctrl+Shift+V sent", false), _overlay);
            }
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
                $$"""{"v":2,"type":"action","id":"{{ActionId}}","name":"custom","key":"lock-pc"}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Contains(("lock", (object?)null)),
                TimeSpan.FromSeconds(10),
                "executor to receive the lock verb");
            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage($$"""recv {"v":2,"type":"ack","id":"{{ActionId}}","ok":true}"""),
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
            // S8-3 macro: stop -> wait 1 ms -> chord, one endpoint fire.
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
                $$"""{"v":2,"type":"action","id":"{{ActionId}}","name":"custom","key":"movie-time"}"""));
            host.SetEnabled(true);

            var expectedChord = new ParsedKeyChord(
                KeyChordModifiers.Ctrl | KeyChordModifiers.Shift, KeyChord.Keys["V"]);
            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Contains(("keySequence", (object?)expectedChord)),
                TimeSpan.FromSeconds(10),
                "executor to receive the macro's final chord step");
            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage($$"""recv {"v":2,"type":"ack","id":"{{ActionId}}","ok":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the ok ack");

            // Both executor steps ran, in configured order (the delay step
            // never touches the executor).
            Assert.Equal(
                [("mediaStop", null), ("keySequence", expectedChord)],
                _executor.Calls.Where(c => c.Name is "mediaStop" or "keySequence"));

            lock (_gate)
            {
                Assert.Contains(new OverlayContent("Google Home → Movie Time", "ran 3 steps", false), _overlay);
            }
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
                $$"""{"v":2,"type":"action","id":"{{ActionId}}","name":"custom","key":"movie-time"}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage("\"ok\":false") && _log.ContainsMessage("step 1 of 2"),
                TimeSpan.FromSeconds(10),
                "stub to receive the nack naming the failing step");

            // Execution stopped at step 1: the second media key never ran.
            Assert.DoesNotContain(("next", (object?)null), _executor.Calls);
        }

        [Fact]
        public async Task SupervisorEnvCarriesTheConfiguredMomentaryResetInterval()
        {
            _config.Current.MomentaryResetMs = 450;
            const string script =
                "console.log('MRESET=' + process.env.HTPC_BRIDGE_MOMENTARY_RESET_MS);" +
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
        }

        [Fact]
        public async Task UnknownCustomKeyNacksWithAReasonAndNeverHitsTheExecutor()
        {
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":2,"type":"action","id":"{{ActionId}}","name":"custom","key":"no-such-key"}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage(
                    $$"""recv {"v":2,"type":"ack","id":"{{ActionId}}","ok":false,"error":"unknown or disabled custom command: no-such-key"}"""),
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
                $$"""{"v":2,"type":"action","id":"{{ActionId}}","name":"custom","key":"movie-mode"}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage(
                    $$"""recv {"v":2,"type":"ack","id":"{{ActionId}}","ok":false,"error":"unknown or disabled custom command: movie-mode"}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the fail ack for the disabled command");

            Assert.Empty(_executor.Calls);
        }

        [Fact]
        public async Task SupersededV1FrameIsRejectedAndClosesTheSocket()
        {
            // Version-bump proof at the wiring level (ADR-004 §3): a stub
            // still speaking v1 authenticates (hello is v2 in the spec below)
            // but its v1 action must close the socket, not execute.
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":1,"type":"action","id":"{{ActionId}}","name":"playPause"}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.Contains("WARN", "\"v\" must be the integer 2"),
                TimeSpan.FromSeconds(10),
                "the v1 frame to be rejected with the version reason");

            Assert.Empty(_executor.Calls);
        }

        [Fact]
        public async Task ActionsFeedTheAppMetricsCountersAndEmitTheDebugTimingLine()
        {
            using var counters = new CounterCapture();
            using var host = CreateHost(NodeClientSpec(
                $$"""{"v":2,"type":"action","id":"{{ActionId}}","name":"setVolume","value":25}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage($$"""recv {"v":2,"type":"ack","id":"{{ActionId}}","ok":true}"""),
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
        }

        [Fact]
        public void FactoryResetOnADisabledBridgeDeletesExistingStorageAndStaysDisabled()
        {
            // S3-2: "disabled-bridge path just deletes" — no sidecar was ever
            // started in this test, so SetEnabled(false) inside FactoryReset
            // is a no-op; only the directory delete does anything.
            string storageDir = Path.Combine(_dir, "matter");
            Directory.CreateDirectory(storageDir);
            File.WriteAllText(Path.Combine(storageDir, "fabric.json"), "{}");

            using BridgeHost host = CreateHost(NodeClientSpec());

            FactoryResetResult result = host.FactoryReset();

            Assert.True(result.Ok);
            Assert.Null(result.Error);
            Assert.False(Directory.Exists(storageDir), "storage dir must be gone");
            Assert.Equal(BridgeState.Disabled, host.State);
            Assert.True(_log.Contains("INFO", "factory reset complete"));
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
                    new OverlayContent("Factory reset complete", "open Pair with Google Home to re-pair", false),
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
        public async Task FactoryResetOnALockedStorageDirFailsGracefullyWithoutDeletingOrRestarting()
        {
            string storageDir = Path.Combine(_dir, "matter");
            Directory.CreateDirectory(storageDir);
            string lockedFile = Path.Combine(storageDir, "locked.db");
            File.WriteAllText(lockedFile, "locked");

            using BridgeHost host = CreateHost(NodeClientSpec());
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => host.State == BridgeState.Connected,
                TimeSpan.FromSeconds(10),
                "bridge to connect before the reset");

            FactoryResetResult result;
            using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // Held open (no FileShare.Delete) for the whole retry window,
                // so the delete must exhaust its retries and fail — never
                // silently, never with a half-deleted directory.
                result = host.FactoryReset();
            }

            Assert.False(result.Ok);
            Assert.NotNull(result.Error);
            Assert.True(Directory.Exists(storageDir), "a failed delete must not partially remove the directory");
            Assert.True(File.Exists(lockedFile), "nothing inside the directory was touched either");
            Assert.True(_log.Contains("WARN", "factory reset could not delete"));

            // Never comes back up on a failed reset — the bridge stays
            // disabled so nothing races a retry with a half-cleared fabric.
            Assert.Equal(BridgeState.Disabled, host.State);
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
                "ws.send(JSON.stringify({ v: 2, type: 'hello', token: t, protocol: 1 }));" +
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

            public event EventHandler<VolumeState>? VolumeChanged;

            public bool NextResult { get; set; } = true;

            public VolumeState State { get; set; } = new(55, false);

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
                }

                return NextResult;
            }

            public VolumeState GetVolumeState() => State;

            public void RaiseVolumeChanged(VolumeState state) => VolumeChanged?.Invoke(this, state);

            public void Dispose()
            {
            }
        }
    }
}
