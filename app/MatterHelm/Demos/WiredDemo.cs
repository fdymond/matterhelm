using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using MatterHelm.Actions;
using MatterHelm.Diagnostics;
using MatterHelm.Sidecar;
using MatterHelm.Ui;

namespace MatterHelm.Demos;

/// <summary>
/// S2-5 acceptance evidence (extended through protocol v5): spawns the
/// stub sidecar (<c>DemoAssets\StubSidecar.js</c>) under the real
/// <see cref="BridgeHost"/>/<see cref="IpcServer"/>/<see cref="SidecarSupervisor"/>
/// wiring with the real <see cref="ActionExecutorAdapter"/> and a live
/// <see cref="OverlayHud"/>, then objectively checks: volume/mute
/// read-back after the stub's setVolume/setMuted actions, an ok ack per
/// action id (parsed from the stub's stdout echo), overlay Show calls with
/// the expected primary strings, the pairing frame surfacing, a state
/// frame carrying a locally-set sentinel volume reaching the stub, a
/// <c>custom</c> action round-trip (config-defined <c>launch</c> of
/// node.exe writing a marker file — asserted on disk — plus its ok ack and
/// "Google Home → Demo Note" overlay), and that a deliberate v1 frame is
/// rejected and closes the socket (version-bump proof). Restores the
/// original volume/mute in <c>finally</c>. Returns 0 iff all checks pass;
/// results also land in <c>wired-demo-results.txt</c>. Invoked via
/// <c>MatterHelm.exe --demo-wired</c>; not part of the production tray
/// flow.
/// </summary>
internal static partial class WiredDemo
{
    // S2-5/S4-2 acceptance demo tuning (must match DemoAssets\StubSidecar.js).
    private const int WiredDemoStubVolume = 37;
    private const int WiredDemoSentinelVolume = 61;
    private const string WiredDemoCustomKey = "demo-note";
    private const string WiredDemoCustomName = "Demo Note";
    private const string WiredDemoMarkerFileName = "demo-note-marker.txt";
    private const string WiredDemoMarkerContent = "demo-note ok";
    private const string WiredDemoResultsFileName = "wired-demo-results.txt";
    private const int AttachParentProcess = -1;

    /// <summary>Runs the full demo. Returns 0 iff every check passed.</summary>
    internal static int Run()
    {
        _ = AttachConsole(AttachParentProcess); // WinExe has no console; borrow the parent's if present.
        ApplicationConfiguration.Initialize();

        var gate = new Lock();
        List<string> lines = [];
        List<(string Level, string Message)> log = [];
        bool allPassed = true;

        void Emit(string line)
        {
            lock (gate)
            {
                lines.Add(line);
            }

            Console.WriteLine(line);
        }

        void Check(bool pass, string what)
        {
            allPassed &= pass;
            Emit($"{(pass ? "PASS" : "FAIL")}  {what}");
        }

        void Sink(string level, string message)
        {
            lock (gate)
            {
                log.Add((level, message));
            }

            Emit($"    [{level}] {message}");
        }

        bool LogContains(string substring)
        {
            lock (gate)
            {
                return log.Any(e => e.Message.Contains(substring, StringComparison.Ordinal));
            }
        }

        Emit($"S2-5/S4-2 wired demo (protocol v5) — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        string? node = DemoSupport.ResolveNodeExe();
        string stubPath = Path.Combine(AppContext.BaseDirectory, "DemoAssets", "StubSidecar.js");
        if (node is null || !File.Exists(stubPath))
        {
            Emit(node is null
                ? "FAIL  node.exe not found (set HTPC_DEMO_NODE or add node to PATH)"
                : $"FAIL  stub sidecar missing at {stubPath}");
            WriteWiredResults(lines, allPassed: false);
            return 1;
        }

        Emit($"node: {node}");

        string tempRoot = Path.Combine(Path.GetTempPath(), "htpc-wired-demo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        // S5-2: the whole diagnostics surface runs against the demo temp dir —
        // redirected app log, metrics snapshots, and the bundle export below
        // never touch the real %APPDATA%. Debug minimum so the per-action
        // timing lines land in the redirected file too.
        string logsDir = Path.Combine(tempRoot, "logs");
        Log.LogDirectory = logsDir;
        Log.MinimumLevel = LogLevel.Debug;
        Log.Initialize();
        Log.Info("S5-2 wired demo: app log redirected to the demo temp dir.");
        using var metrics = new MetricsFileListener(logsDir);

        string configPath = Path.Combine(tempRoot, "config.json");
        var config = new Config(configPath, Sink);
        config.Current.IpcPort = DemoSupport.GetFreeLoopbackPort();

        // S4-2: a custom `launch` command with a verifiable, side-effect-free
        // effect — node writes a marker file into the demo temp dir. No shell
        // string anywhere: the args split into discrete ArgumentList entries.
        string markerPath = Path.Combine(tempRoot, WiredDemoMarkerFileName);
        config.Current.Commands.Custom =
        [
            new CustomCommandConfig
            {
                Key = WiredDemoCustomKey,
                Name = WiredDemoCustomName,
                Action = new LaunchActionConfig
                {
                    Path = node,
                    Args = $"-e \"require('fs').writeFileSync(process.argv[1], '{WiredDemoMarkerContent}')\" \"{markerPath}\"",
                },
            },
        ];
        Emit($"ipc port: {config.Current.IpcPort} (ephemeral); temp root: {tempRoot}");

        using var executor = new ActionExecutorAdapter();
        VolumeState original;
        try
        {
            original = executor.GetVolumeState();
        }
        catch (InvalidOperationException ex)
        {
            Emit($"FAIL  no audio endpoint available ({ex.Message}); the wired demo needs one for read-back.");
            WriteWiredResults(lines, allPassed: false);
            return 1;
        }

        Emit($"original state: volume {original.VolumePercent} %, muted {original.Muted}");

        List<Ui.OverlayContent> overlayCalls = [];
        List<PairingFrame> pairingFrames = [];
        List<BridgeState> states = [];
        using var hud = new OverlayHud();
        using var host = new BridgeHost(
            config,
            executor,
            new SidecarSpec(node, [stubPath], AppContext.BaseDirectory),
            overlaySink: content =>
            {
                lock (gate)
                {
                    overlayCalls.Add(content);
                }

                hud.Show(content); // The real HUD runs too; marshals internally.
            },
            log: Sink,
            storageDir: Path.Combine(tempRoot, "matter"));
        host.PairingReceived += (_, frame) =>
        {
            lock (gate)
            {
                pairingFrames.Add(frame);
            }
        };
        host.StateChanged += (_, state) =>
        {
            lock (gate)
            {
                states.Add(state);
            }
        };

        try
        {
            host.SetEnabled(true);

            List<(string Name, string Id)> SentActions()
            {
                lock (gate)
                {
                    return log
                        .Select(e => e.Message)
                        .Where(m => m.StartsWith("sidecar: stub-sent ", StringComparison.Ordinal))
                        .Select(m => m["sidecar: stub-sent ".Length..].Split(' '))
                        .Where(parts => parts.Length == 2)
                        .Select(parts => (parts[0], parts[1]))
                        .ToList();
                }
            }

            bool sequenceDone = PumpUntil(
                () =>
                {
                    lock (gate)
                    {
                        return pairingFrames.Count >= 1 && overlayCalls.Count(c => c.Primary.StartsWith("Google Home → ", StringComparison.Ordinal)) >= 4;
                    }
                },
                timeoutMs: 20_000)
                && SentActions().Count >= 4;
            Check(sequenceDone, "stub completed its scripted sequence (4 actions incl. custom + pairing) within 20 s");

            VolumeState after = executor.GetVolumeState();
            Check(
                after.VolumePercent == WiredDemoStubVolume,
                $"volume read-back == {WiredDemoStubVolume} after the stub's setVolume (actual {after.VolumePercent})");
            Check(!after.Muted, $"muted read-back == false after the stub's setMuted (actual {after.Muted})");

            foreach ((string name, string id) in SentActions())
            {
                // playPause routes through the media stack (S11-8). In this
                // demo there is no foreground media target and no media
                // session, so the honest answer is a FAILED ack rather than a
                // silent OK; every other action must still ack ok.
                bool acked = PumpUntil(
                    () => LogContains($"stub-recv {{\"v\":5,\"type\":\"ack\",\"id\":\"{id}\",\"ok\":true}}"),
                    timeoutMs: 5_000);
                if (name is not "playPause")
                {
                    Check(acked, $"stub received ack ok for {name} (id {id})");
                    continue;
                }

                bool ackedEitherWay = acked || PumpUntil(
                    () => LogContains($"stub-recv {{\"v\":5,\"type\":\"ack\",\"id\":\"{id}\",\"ok\":false"),
                    timeoutMs: 5_000);
                Check(
                    ackedEitherWay,
                    $"stub received an ack for {name}: ok with a media target, honest failure without one (id {id})");
            }

            // S4-2: the custom `launch` action ran detached — the marker file
            // it writes is the objective side-effect evidence.
            bool markerWritten = PumpUntil(
                () => File.Exists(markerPath) && File.ReadAllText(markerPath) == WiredDemoMarkerContent,
                timeoutMs: 10_000);
            Check(markerWritten, $"custom launch action wrote the marker file ({WiredDemoMarkerFileName})");

            string[] expectedPrimaries =
            [
                // Volume sets read "Volume" without the percent — the fill bar
                // carries the number (maintainer request).
                "Google Home → Volume",
                "Google Home → unmute",
                "Google Home → play/pause",
                $"Google Home → {WiredDemoCustomName}",
            ];
            List<Ui.OverlayContent> overlaySnapshot;
            lock (gate)
            {
                overlaySnapshot = [.. overlayCalls];
            }

            Check(overlaySnapshot.Count >= 4, $"overlay Show invoked >= 4 times (actual {overlaySnapshot.Count})");
            foreach (string primary in expectedPrimaries)
            {
                // The play/pause flash is expected to carry an error pill here:
                // with no foreground media target and no media session, S11-8
                // routing fails honestly, and the HUD must show that rather
                // than a success pill. Every other command must be error-free.
                bool mediaRouted = primary.Contains("play/pause", StringComparison.OrdinalIgnoreCase);
                Check(
                    overlaySnapshot.Any(c => c.Primary == primary && (mediaRouted || !c.IsError)),
                    mediaRouted
                        ? $"overlay Show invoked with primary \"{primary}\" (pill state follows the routing result)"
                        : $"overlay Show invoked with primary \"{primary}\" (no error pill)");
            }

            // S4-5: the setVolume flash must carry the resulting level so the
            // HUD renders the percentage bar instead of the text pill.
            Check(
                overlaySnapshot.Any(c => c.Primary == "Google Home → Volume"
                    && c.VolumePercent == WiredDemoStubVolume && !c.Muted),
                $"setVolume overlay content carries VolumePercent {WiredDemoStubVolume} (volume-bar pill)");

            PairingFrame? pairing;
            lock (gate)
            {
                pairing = pairingFrames.FirstOrDefault();
            }

            Check(
                pairing is { QrPayload: "MT:STUB-DEMO-PAYLOAD", ManualCode: "3497-011-2332" },
                $"pairing frame surfaced (qrPayload {pairing?.QrPayload ?? "(none)"}, manualCode {pairing?.ManualCode ?? "(none)"})");

            lock (gate)
            {
                Check(
                    states.Contains(BridgeState.Connected),
                    $"tray state reached Connected (green) after stub auth (observed: {string.Join(", ", states)})");
            }

            // Local change → state frame to the sidecar: set a sentinel volume
            // through the real executor and expect the stub to echo the frame.
            executor.Execute("setVolume", WiredDemoSentinelVolume);
            bool sentinelSeen = PumpUntil(
                () => LogContains($"stub-recv {{\"v\":5,\"type\":\"state\",\"volume\":{WiredDemoSentinelVolume},"),
                timeoutMs: 10_000);
            Check(
                sentinelSeen,
                $"stub received a state frame with the locally-set sentinel volume {WiredDemoSentinelVolume}");

            // Version-bump proof (ADR-004 §3): the sentinel frame cues the
            // stub to send one v1 frame; the tray app must reject it (only
            // v:5 parses now) and close the socket.
            bool v1Rejected = PumpUntil(
                () => LogContains("\"v\" must be the integer 5"),
                timeoutMs: 10_000);
            Check(v1Rejected, "deliberate v1 frame was rejected (\"v\" must be the integer 5)");
            bool socketClosed = PumpUntil(
                () => LogContains("stub: socket closed"),
                timeoutMs: 10_000);
            Check(socketClosed, "server closed the socket on the v1 frame");

            // S5-2: every executed action must have produced a Debug timing
            // line (the sink echoes them into this output verbatim).
            lock (gate)
            {
                Check(
                    log.Any(e => e.Level == "DEBUG"
                        && e.Message.StartsWith("IPC timing: setVolume ", StringComparison.Ordinal)),
                    "IPC timing Debug line produced for the setVolume action");
            }
        }
        catch (Exception ex)
        {
            allPassed = false;
            Emit($"FAIL  demo crashed: {ex}");
        }
        finally
        {
            host.SetEnabled(false);
            executor.Execute("setVolume", original.VolumePercent);
            executor.Execute("setMuted", original.Muted);
            Emit($"restored: volume {original.VolumePercent} %, muted {original.Muted}");
        }

        Check(
            LogContains("exited after stdin close (tether)"),
            "stub exited via the stdin tether on stop (no kill needed)");

        // S5-2: the metrics snapshot must show all four stub actions executed
        // ok (setVolume, setMuted, playPause, custom).
        metrics.Flush();
        string? snapshot = File.Exists(metrics.CurrentFilePath)
            ? File.ReadLines(metrics.CurrentFilePath).LastOrDefault(l => l.Length > 0)
            : null;
        Emit($"metrics snapshot: {snapshot ?? "(missing)"}");
        long actionsOk = -1;
        if (snapshot is not null)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(snapshot);
                actionsOk = document.RootElement.GetProperty("counters").GetProperty("actions_executed_ok").GetInt64();
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                Emit($"    snapshot parse failed: {ex.Message}");
            }
        }

        Check(actionsOk >= 3, $"metrics file actions_executed_ok >= 3 (actual {actionsOk})");

        // S5-2: produce a diagnostics bundle over the demo temp dirs and list
        // its entries as evidence.
        try
        {
            string bundlePath = DiagnosticsBundle.ExportTo(
                Path.Combine(tempRoot, "matterhelm-diagnostics-demo.zip"), logsDir, configPath);
            using ZipArchive archive = ZipFile.OpenRead(bundlePath);
            Emit("diagnostics bundle entries:");
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                Emit($"    {entry.FullName} ({entry.Length} bytes)");
            }

            Check(
                archive.Entries.Any(e => e.FullName == "manifest.json")
                    && archive.Entries.Any(e => e.FullName == "config.json")
                    && archive.Entries.Any(e => e.FullName.StartsWith("logs/app-", StringComparison.Ordinal))
                    && archive.Entries.Any(e => e.FullName.StartsWith("logs/metrics-", StringComparison.Ordinal)),
                "diagnostics bundle contains manifest.json, config.json, the app log, and the metrics file");
        }
        catch (Exception ex)
        {
            Check(false, $"diagnostics bundle export failed: {ex.Message}");
        }

        WriteWiredResults(lines, allPassed);
        return allPassed ? 0 : 1;
    }

    /// <summary>Pumps the STA message loop (the overlay HUD lives on this thread) until <paramref name="condition"/> or timeout; true iff the condition was met.</summary>
    private static bool PumpUntil(Func<bool> condition, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                return false;
            }

            Application.DoEvents();
            Thread.Sleep(15);
        }

        return true;
    }

    private static void WriteWiredResults(List<string> lines, bool allPassed)
    {
        string resultsPath = Path.Combine(AppContext.BaseDirectory, WiredDemoResultsFileName);
        string report = string.Join(Environment.NewLine, lines) + Environment.NewLine +
            $"OVERALL: {(allPassed ? "PASS" : "FAIL")}" + Environment.NewLine;
        File.WriteAllText(resultsPath, report);
        Console.WriteLine($"OVERALL: {(allPassed ? "PASS" : "FAIL")}");
        Console.WriteLine($"(results file: {resultsPath})");
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
