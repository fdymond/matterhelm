using HtpcMatterBridge.Actions;
using HtpcMatterBridge.Sidecar;
using Xunit;

namespace HtpcMatterBridge.Tests;

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
        private readonly object _gate = new();
        private readonly List<(string Primary, string Pill, bool IsError)> _overlay = [];
        private readonly List<BridgeState> _states = [];
        private readonly List<PairingFrame> _pairings = [];

        public Wiring()
        {
            _dir = Path.Combine(Path.GetTempPath(), "HtpcMatterBridgeTests", Guid.NewGuid().ToString("N"));
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
                $$"""{"v":1,"type":"action","id":"{{ActionId}}","name":"setVolume","value":25}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _executor.Calls.Contains(("setVolume", (object?)25)),
                TimeSpan.FromSeconds(10),
                "executor to receive setVolume 25");
            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage($$"""recv {"v":1,"type":"ack","id":"{{ActionId}}","ok":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the ok ack");

            lock (_gate)
            {
                Assert.Contains(("Google Home → volume 25 %", "volume set to 25 %", false), _overlay);
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
                $$"""{"v":1,"type":"action","id":"{{ActionId}}","name":"setVolume","value":25}"""));
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage(
                    $$"""recv {"v":1,"type":"ack","id":"{{ActionId}}","ok":false,"error":"action failed: volume 25 %"}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the fail ack with the intent text");

            lock (_gate)
            {
                Assert.Contains(("Google Home → volume 25 %", "failed", true), _overlay);
            }
        }

        [Fact]
        public async Task StateFrameIsSentOnConnectAndOnEveryVolumeChange()
        {
            _executor.State = new VolumeState(55, false);
            using var host = CreateHost(NodeClientSpec()); // hello only
            host.SetEnabled(true);

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage("""recv {"v":1,"type":"state","volume":55,"muted":false}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the on-connect state snapshot");

            _executor.RaiseVolumeChanged(new VolumeState(61, true));

            await TestSupport.WaitUntilAsync(
                () => _log.ContainsMessage("""recv {"v":1,"type":"state","volume":61,"muted":true}"""),
                TimeSpan.FromSeconds(10),
                "stub to receive the volume-change state frame");
        }

        [Fact]
        public async Task PairingFrameSurfacesWithPayloadAndFlashesTheOverlay()
        {
            using var host = CreateHost(NodeClientSpec(
                """{"v":1,"type":"pairing","qrPayload":"MT:TEST","manualCode":"1111-222-3333"}"""));
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

        private BridgeHost CreateHost(SidecarSpec spec)
        {
            var host = new BridgeHost(
                _config,
                _executor,
                spec,
                overlaySink: (primary, pill, isError) =>
                {
                    lock (_gate)
                    {
                        _overlay.Add((primary, pill, isError));
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
                "ws.send(JSON.stringify({ v: 1, type: 'hello', token: t, protocol: 1 }));" +
                string.Concat(framesAfterHello.Select(frame => $"ws.send('{frame}');")) +
                "});" +
                "process.stdin.resume();" +
                "process.stdin.on('end', () => process.exit(0));" +
                "setInterval(() => {}, 1000);";
            return new SidecarSpec(TestSupport.RequireNodeExe(), ["-e", script], Path.GetTempPath());
        }

        /// <summary>Thread-safe no-side-effect <see cref="IActionExecutor"/> with scriptable results and volume state.</summary>
        private sealed class FakeExecutor : IActionExecutor
        {
            private readonly object _gate = new();
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
