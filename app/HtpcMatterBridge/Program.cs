using System.Drawing.Imaging;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using HtpcMatterBridge.Actions;
using HtpcMatterBridge.Sidecar;
using HtpcMatterBridge.Ui;

namespace HtpcMatterBridge;

/// <summary>Composition root: enforces single instance, then wires <see cref="TrayContext"/> to <see cref="BridgeHost"/> and runs the tray.</summary>
internal static class Program
{
    // Session-local (not "Global\") mutex is sufficient: this app is per-user,
    // per-session tray tooling, not a service shared across RDP sessions.
    private const string MutexName = "HtpcMatterBridge.SingleInstance";

    // S2-4 acceptance demo tuning.
    private const string DemoQrPayload = "MT:Y.K90C0R159FZO62N10";
    private const string DemoManualCode = "0434-914-6415";
    private const int DemoDisplayMilliseconds = 1_000;
    private const int MinDistinctSampledColors = 2;

    // S2-5 acceptance demo tuning (must match DemoAssets\StubSidecar.js).
    private const int WiredDemoStubVolume = 37;
    private const int WiredDemoSentinelVolume = 61;
    private const string WiredDemoResultsFileName = "wired-demo-results.txt";
    private const int AttachParentProcess = -1;

    /// <summary>Entry point.</summary>
    [STAThread]
    private static void Main(string[] args)
    {
        // S2-2 acceptance demo: run the Actions/ self-test and exit — no tray,
        // no single-instance guard (it must work beside a running instance).
        if (args.Contains("--selftest-actions"))
        {
            Environment.ExitCode = Actions.ActionsSelfTest.Run(
                includeDisplayTests: args.Contains("--selftest-actions-display"));
            return;
        }

        // S2-3 acceptance demo: objectively prove the overlay HUD never
        // steals focus or blocks clicks. Not part of the production tray flow.
        if (args.Contains("--demo-overlay"))
        {
            Environment.ExitCode = Ui.OverlayHudDemo.Run();
            return;
        }

        // Design aid: export the runtime-drawn tray icons (all states, both
        // taskbar themes, several sizes + a contact sheet) for visual review.
        if (args.Contains("--export-tray-icons"))
        {
            Ui.TrayIcons.ExportPreviews();
            return;
        }

        // S2-1 acceptance demo: sidecar crash/auto-restart backoff plus the
        // wrong-token socket close, with objective PASS/FAIL output.
        if (args.Contains("--demo-sidecar-chaos"))
        {
            Environment.ExitCode = Sidecar.SidecarChaosDemo.Run();
            return;
        }

        // S2-4 acceptance demo: open the pairing window with a sample payload,
        // screenshot it, and objectively verify the QR actually rendered
        // (non-trivial pixel variance) before exiting. Not part of the
        // production tray flow.
        if (args.Contains("--demo-pairing-window"))
        {
            Environment.ExitCode = RunPairingWindowDemo();
            return;
        }

        // S2-5 acceptance demo: full mock-sidecar E2E — stub node sidecar over
        // the real WS protocol → executor side effects (read back) → overlay →
        // acks → state frames, with objective PASS/FAIL output. Uses its own
        // temp config/storage and an ephemeral port, never %APPDATA%.
        if (args.Contains("--demo-wired"))
        {
            Environment.ExitCode = RunWiredDemo();
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out bool createdNew);
        if (!createdNew)
        {
            // Another instance already owns the mutex; exit silently (no dialog, no log spam).
            return;
        }

        Log.Initialize();
        Log.Info("HtpcMatterBridge starting.");

        ApplicationConfiguration.Initialize();

        var trayContext = new TrayContext();
        using var executor = new ActionExecutorAdapter();
        using var overlay = new OverlayHud { Visible = trayContext.Config.Current.OverlayEnabled };
        using var host = new BridgeHost(
            trayContext.Config,
            executor,
            SidecarLaunchSpec.Default(),
            overlaySink: overlay.Show);

        // TrayContext.SetState/SetPairingInfo marshal to the UI thread
        // internally, so BridgeHost's pool-thread events forward directly.
        host.StateChanged += (_, state) => trayContext.SetState(state);
        host.PairingReceived += (_, pairing) => trayContext.SetPairingInfo(pairing.QrPayload, pairing.ManualCode);

        // Start/stop happen on a worker: SetEnabled blocks for the child's
        // stop grace, and the UI thread must never wait on that.
        trayContext.EnableBridgeChanged += (_, enabled) => Task.Run(() => host.SetEnabled(enabled));
        trayContext.OverlayEnabledChanged += (_, enabled) => overlay.Visible = enabled;

        // Exit is the one sanctioned synchronous stop: the sidecar must be
        // down (stdin tether, then kill) before the process goes away.
        trayContext.ExitRequested += (_, _) => host.SetEnabled(false);

        if (trayContext.Config.Current.BridgeEnabled)
        {
            Task.Run(() => host.SetEnabled(true));
        }

        Application.Run(trayContext);

        Log.Info("HtpcMatterBridge exited.");
    }

    /// <summary>
    /// S2-5 acceptance evidence: spawns the stub sidecar
    /// (<c>DemoAssets\StubSidecar.js</c>) under the real
    /// <see cref="BridgeHost"/>/<see cref="IpcServer"/>/<see cref="SidecarSupervisor"/>
    /// wiring with the real <see cref="ActionExecutorAdapter"/> and a live
    /// <see cref="OverlayHud"/>, then objectively checks: volume/mute
    /// read-back after the stub's setVolume/setMuted actions, an ok ack per
    /// action id (parsed from the stub's stdout echo), overlay Show calls with
    /// the expected primary strings, the pairing frame surfacing, and a state
    /// frame carrying a locally-set sentinel volume reaching the stub.
    /// Restores the original volume/mute in <c>finally</c>. Returns 0 iff all
    /// checks pass; results also land in <c>wired-demo-results.txt</c>.
    /// </summary>
    private static int RunWiredDemo()
    {
        _ = AttachConsole(AttachParentProcess); // WinExe has no console; borrow the parent's if present.
        ApplicationConfiguration.Initialize();

        var gate = new object();
        var lines = new List<string>();
        var log = new List<(string Level, string Message)>();
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

        Emit($"S2-5 wired demo — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        string? node = ResolveNodeExe();
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
        var config = new Config(Path.Combine(tempRoot, "config.json"), Sink);
        config.Current.IpcPort = GetFreeLoopbackPort();
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

        var overlayCalls = new List<(string Primary, string Pill, bool IsError)>();
        var pairingFrames = new List<PairingFrame>();
        var states = new List<BridgeState>();
        using var hud = new OverlayHud();
        using var host = new BridgeHost(
            config,
            executor,
            new SidecarSpec(node, [stubPath], AppContext.BaseDirectory),
            overlaySink: (primary, pill, isError) =>
            {
                lock (gate)
                {
                    overlayCalls.Add((primary, pill, isError));
                }

                hud.Show(primary, pill, isError); // The real HUD runs too; marshals internally.
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
                        return pairingFrames.Count >= 1 && overlayCalls.Count(c => c.Primary.StartsWith("Google Home → ", StringComparison.Ordinal)) >= 3;
                    }
                },
                timeoutMs: 20_000)
                && SentActions().Count >= 3;
            Check(sequenceDone, "stub completed its scripted sequence (3 actions + pairing) within 20 s");

            VolumeState after = executor.GetVolumeState();
            Check(
                after.VolumePercent == WiredDemoStubVolume,
                $"volume read-back == {WiredDemoStubVolume} after the stub's setVolume (actual {after.VolumePercent})");
            Check(!after.Muted, $"muted read-back == false after the stub's setMuted (actual {after.Muted})");

            foreach ((string name, string id) in SentActions())
            {
                bool acked = PumpUntil(
                    () => LogContains($"stub-recv {{\"v\":1,\"type\":\"ack\",\"id\":\"{id}\",\"ok\":true}}"),
                    timeoutMs: 5_000);
                Check(acked, $"stub received ack ok for {name} (id {id})");
            }

            string[] expectedPrimaries =
            [
                $"Google Home → volume {WiredDemoStubVolume} %",
                "Google Home → unmute",
                "Google Home → play/pause",
            ];
            List<(string Primary, string Pill, bool IsError)> overlaySnapshot;
            lock (gate)
            {
                overlaySnapshot = [.. overlayCalls];
            }

            Check(overlaySnapshot.Count >= 3, $"overlay Show invoked >= 3 times (actual {overlaySnapshot.Count})");
            foreach (string primary in expectedPrimaries)
            {
                Check(
                    overlaySnapshot.Any(c => c.Primary == primary && !c.IsError),
                    $"overlay Show invoked with primary \"{primary}\" (no error pill)");
            }

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
                () => LogContains($"stub-recv {{\"v\":1,\"type\":\"state\",\"volume\":{WiredDemoSentinelVolume},"),
                timeoutMs: 10_000);
            Check(
                sentinelSeen,
                $"stub received a state frame with the locally-set sentinel volume {WiredDemoSentinelVolume}");
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

    /// <summary>Demo-only node resolution (HTPC_DEMO_NODE, then PATH, then <c>%USERPROFILE%\tools\node-*</c>) — same policy as the S2-1 chaos demo.</summary>
    private static string? ResolveNodeExe()
    {
        string? env = Environment.GetEnvironmentVariable("HTPC_DEMO_NODE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
        {
            return env;
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null)
        {
            foreach (string dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir, "node.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry; skip.
                }
            }
        }

        string tools = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "tools");
        if (Directory.Exists(tools))
        {
            foreach (string dir in Directory.EnumerateDirectories(tools, "node-*"))
            {
                string candidate = Path.Combine(dir, "node.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Reserves a currently-free loopback port (HttpListener cannot bind port 0 itself).</summary>
    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// S2-4 acceptance evidence: opens <see cref="Ui.PairingWindow"/> with a
    /// sample commissioning payload, lets it lay out, screenshots it to
    /// <c>pairing-window-demo.png</c> next to the exe, and objectively checks
    /// that the QR area actually rendered (non-trivial pixel variance, not a
    /// blank box) before closing. Returns 0 iff that check passes.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="Control.DrawToBitmap(Bitmap, Rectangle)"/> rather than
    /// <c>Graphics.CopyFromScreen</c>: this harness's session can have other
    /// content actually composited on top of (or instead of) our window on the
    /// real screen buffer — a desktop-capture screenshot would then silently
    /// verify the wrong pixels (observed directly: a raw screen capture here
    /// came back showing unrelated desktop content, not this window).
    /// <c>DrawToBitmap</c> renders the control's own client area via
    /// <c>WM_PRINT</c>, independent of on-screen Z-order/occlusion, so it is
    /// the reliable way for a window to "screenshot itself" in this environment.
    /// </remarks>
    private static int RunPairingWindowDemo()
    {
        ApplicationConfiguration.Initialize();

        using var window = new Ui.PairingWindow();
        window.SetPairingInfo(DemoQrPayload, DemoManualCode);
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(80, 80);
        window.Show();
        window.Activate();

        Pump(DemoDisplayMilliseconds);

        using var windowBitmap = new Bitmap(window.ClientSize.Width, window.ClientSize.Height);
        window.DrawToBitmap(windowBitmap, new Rectangle(Point.Empty, window.ClientSize));

        string outputPath = Path.Combine(AppContext.BaseDirectory, "pairing-window-demo.png");
        windowBitmap.Save(outputPath, ImageFormat.Png);

        using Bitmap qrBitmap = windowBitmap.Clone(window.QrImageBounds, windowBitmap.PixelFormat);
        bool hasVariance = HasPixelVariance(qrBitmap, MinDistinctSampledColors);

        Console.WriteLine($"[{(hasVariance ? "PASS" : "FAIL")}] QR image rendered with non-trivial pixel variance.");
        Console.WriteLine($"(screenshot: {outputPath})");

        window.Close();
        return hasVariance ? 0 : 1;
    }

    /// <summary>Pumps the STA message loop for at least <paramref name="milliseconds"/> so the window finishes laying out (no <c>Application.Run</c> is active in this demo).</summary>
    private static void Pump(int milliseconds)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        do
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
        while (Environment.TickCount64 < deadline);
    }

    /// <summary>True iff a sampled grid of pixels contains at least <paramref name="minDistinctColors"/> distinct ARGB values — evidence of an actually-rendered QR rather than a blank/uniform box.</summary>
    private static bool HasPixelVariance(Bitmap bitmap, int minDistinctColors)
    {
        var distinctColors = new HashSet<int>();
        int stepX = Math.Max(1, bitmap.Width / 100);
        int stepY = Math.Max(1, bitmap.Height / 100);
        for (int y = 0; y < bitmap.Height; y += stepY)
        {
            for (int x = 0; x < bitmap.Width; x += stepX)
            {
                distinctColors.Add(bitmap.GetPixel(x, y).ToArgb());
            }
        }

        return distinctColors.Count >= minDistinctColors;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
