using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using MatterHelm.Actions;
using MatterHelm.Sidecar;
using MatterHelm.Ui;

namespace MatterHelm.Diagnostics;

/// <summary>
/// S6-1 resource-hygiene probe (<c>--probe-resources</c>, hidden branch in
/// <c>Program.Main</c>): exercises the churn-prone UI paths — overlay HUD
/// flashes with alternating content widths (each width change tears down and
/// re-creates the layered DIB canvas), settings-window open/close (full
/// control tree build + event subscribe/unsubscribe), and tray SetState icon
/// cycles — then asserts that handle count, GDI/USER objects, and private
/// bytes stay bounded between a warmed-up "before" sample and the "after"
/// sample. Unbounded growth here is a native leak (DIB/DC/font/icon) that
/// idle-memory numbers never show. Runs against a temp config/log dir, never
/// <c>%APPDATA%</c>. Deliberately NOT a working-set exercise: paging games
/// (MinWorkingSet trims) are fake wins and are asserted on nothing here —
/// private bytes and object counts are what leak.
/// </summary>
internal static partial class ResourceProbe
{
    private const int OverlayIterations = 500;
    private const int SettingsIterations = 100;
    private const int TrayStateCycles = 50;
    private const int WizardIterations = 50;
    private const int BridgeLifecycleCycles = 5;
    private const int MacroBurstSize = 12;

    // Drift budgets (story S6-1): churn may cost a few cached OS objects
    // (font realizations, message-window handles) but nothing proportional
    // to the iteration counts.
    private const int MaxHandleDrift = 20;
    private const int MaxGdiDrift = 20;
    private const int MaxUserDrift = 20;
    private const double MaxPrivateGrowthFraction = 0.10;

    private const uint GrGdiObjects = 0;
    private const uint GrUserObjects = 1;

    private const string ResultsFileName = "resource-probe-results.txt";

    /// <summary>Runs the probe; returns 0 iff every bound held.</summary>
    internal static int Run()
    {
        List<string> lines = [];
        bool allPassed = true;

        void Emit(string line)
        {
            lines.Add(line);
            Console.WriteLine(line);
        }

        void Check(bool pass, string what)
        {
            allPassed &= pass;
            Emit($"{(pass ? "PASS" : "FAIL")}  {what}");
        }

        Emit($"S6-1 resource probe — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        string tempRoot = Path.Combine(Path.GetTempPath(), "htpc-resource-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        Log.LogDirectory = Path.Combine(tempRoot, "logs");
        Log.Initialize();
        Emit($"temp root: {tempRoot}");

        var config = new Config(
            Path.Combine(tempRoot, "config.json"),
            (level, message) => Emit($"    [{level}] {message}"));
        config.Current.Commands.Custom =
        [
            new CustomCommandConfig
            {
                Key = "probe-macro",
                Name = "Probe macro",
                Action = new SequenceActionConfig
                {
                    Steps =
                    [
                        new DelayActionConfig { Ms = 1_000 },
                        new MediaKeyActionConfig { KeyName = MediaKeyName.Stop },
                    ],
                },
            },
        ];

        using var hud = new OverlayHud();
        var tray = new TrayContext(config); // never disposed: ApplicationContext teardown is the Exit path; the probe process exits instead.

        void OverlayChurn(int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                // Alternate short/long primaries so MeasureDesiredCanvasWidth
                // lands in different quantized widths — every call re-creates
                // the DIB canvas (the churn path under test). Every third
                // flash renders the volume bar instead of the text pill.
                OverlayContent content = (i % 3) switch
                {
                    0 => new OverlayContent("Google Home → Volume", "ok", IsError: false) { VolumePercent = i % 101 },
                    1 => new OverlayContent(
                        $"Google Home → a deliberately long primary line to force a wide canvas ({i})",
                        "executed the long command",
                        IsError: false),
                    _ => new OverlayContent("HTPC → ok", "done", IsError: i % 2 == 0),
                };
                hud.Show(content);
                Pump(1);
            }
        }

        void SettingsChurn(int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                using var window = new SettingsWindow(new SettingsViewModel(config));
                window.StartPosition = FormStartPosition.Manual;
                window.Location = new Point(40, 40);
                window.Show();
                Pump(5);
                window.Close();
                Pump(1);
            }
        }

        void TrayChurn(int cycles)
        {
            BridgeState[] states =
                [BridgeState.Running, BridgeState.Connected, BridgeState.Faulted, BridgeState.Disabled];
            for (int i = 0; i < cycles; i++)
            {
                foreach (BridgeState state in states)
                {
                    tray.SetState(state);
                    Pump(1);
                }
            }
        }

        void WizardChurn(int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                using (var pairing = new PairingWindow())
                {
                    pairing.Show();
                    Pump(1);
                    pairing.Close();
                }

                using (var welcome = new WelcomeWindow())
                {
                    welcome.Show();
                    Pump(1);
                    welcome.Close();
                }
            }
        }

        void BridgeChurn(int cycles)
        {
            using var executor = new ProbeExecutor();
            using var host = new BridgeHost(
                config,
                executor,
                ProbeSidecarSpec(),
                supervisorOptions: new Sidecar.SupervisorOptions { StopGraceMs = 500 },
                log: (level, message) => Emit($"    [{level}] {message}"),
                storageDir: Path.Combine(tempRoot, "matter"));
            for (int i = 0; i < cycles; i++)
            {
                config.Current.IpcPort = GetFreeLoopbackPort();
                host.SetEnabled(true);
                long deadline = Environment.TickCount64 + 2_000;
                while (host.State != BridgeState.Connected && Environment.TickCount64 < deadline)
                {
                    Pump(5);
                }

                host.SetEnabled(false);
            }
        }

        try
        {
            // Idle composition sample first (task-2 evidence): tray + HUD up,
            // no churn yet, GC settled.
            Pump(2_000);
            ResourceSample idle = Sample();
            Emit($"idle composition (tray + overlay, no churn): {idle}");

            // Warm-up: JIT, font caches, first-window costs — outside the
            // measured window so the before/after delta is pure churn.
            OverlayChurn(25);
            SettingsChurn(5);
            TrayChurn(3);
            WizardChurn(2);
            BridgeChurn(1);

            // Two identical measured blocks. The first block still absorbs
            // one-time commit growth (the GC retains expanded heap segments
            // even after a compacting collect, caches reach capacity), so the
            // bounded-growth assertion runs on the SECOND block: a real
            // per-iteration leak grows both blocks equally; steady-state
            // infrastructure growth shows only in the first. Both are
            // reported.
            void ChurnBlock(string label)
            {
                OverlayChurn(OverlayIterations);
                SettingsChurn(SettingsIterations);
                TrayChurn(TrayStateCycles);
                WizardChurn(WizardIterations);
                BridgeChurn(BridgeLifecycleCycles);
                Emit($"{label}: {OverlayIterations}x overlay Show, {SettingsIterations}x settings, {TrayStateCycles}x tray states, {WizardIterations}x pairing+welcome recreates, {BridgeLifecycleCycles}x bridge stop/start with {MacroBurstSize} macros/start");
            }

            ResourceSample before = Sample();
            Emit($"before block 1: {before}");
            ChurnBlock("block 1 done");
            ResourceSample mid = Sample();
            Emit($"after block 1:  {mid}  (drift: {Drift(before, mid)})");
            ChurnBlock("block 2 done");
            ResourceSample after = Sample();
            Emit($"after block 2:  {after}  (drift: {Drift(mid, after)})");

            long handleDrift = after.Handles - mid.Handles;
            long gdiDrift = after.Gdi - mid.Gdi;
            long userDrift = after.User - mid.User;
            double privateGrowth = (after.PrivateBytes - mid.PrivateBytes) / (double)mid.PrivateBytes;
            Check(handleDrift < MaxHandleDrift, $"steady-state handle drift {handleDrift} < {MaxHandleDrift}");
            Check(gdiDrift < MaxGdiDrift, $"steady-state GDI object drift {gdiDrift} < {MaxGdiDrift}");
            Check(userDrift < MaxUserDrift, $"steady-state USER object drift {userDrift} < {MaxUserDrift}");
            Check(
                privateGrowth < MaxPrivateGrowthFraction,
                $"steady-state private bytes growth {privateGrowth:P1} < {MaxPrivateGrowthFraction:P0} ({mid.PrivateBytes / 1024 / 1024.0:0.0} MB → {after.PrivateBytes / 1024 / 1024.0:0.0} MB)");
        }
        catch (Exception ex)
        {
            allPassed = false;
            Emit($"FAIL  probe crashed: {ex}");
        }

        Emit($"OVERALL: {(allPassed ? "PASS" : "FAIL")}");
        string resultsPath = Path.Combine(AppContext.BaseDirectory, ResultsFileName);
        File.WriteAllText(resultsPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        Console.WriteLine($"(results file: {resultsPath})");
        return allPassed ? 0 : 1;
    }

    /// <summary>Minimal authenticated client used only by the resource probe's child processes.</summary>
    internal static int RunSidecar()
    {
        try
        {
            string port = Environment.GetEnvironmentVariable("HTPC_BRIDGE_IPC_PORT")
                ?? throw new InvalidOperationException("resource probe sidecar has no IPC port");
            string token = Environment.GetEnvironmentVariable("HTPC_BRIDGE_IPC_TOKEN")
                ?? throw new InvalidOperationException("resource probe sidecar has no IPC token");
            using var socket = new ClientWebSocket();
            socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None).GetAwaiter().GetResult();
            Send(socket, $$"""{"v":5,"type":"hello","token":"{{token}}","protocol":1}""");
            Send(socket, """{"v":5,"type":"matterStatus","commissioned":true,"advertisement":"notApplicable"}""");
            for (int i = 0; i < MacroBurstSize; i++)
            {
                Send(socket, $$"""{"v":5,"type":"action","id":"{{Guid.NewGuid()}}","name":"custom","key":"probe-macro","on":true}""");
            }

            _ = Console.In.ReadToEnd();
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static void Send(ClientWebSocket socket, string frame)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(frame);
        socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private static SidecarSpec ProbeSidecarSpec()
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("resource probe cannot resolve its executable path");
        string[] args = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [Environment.GetCommandLineArgs()[0], "--resource-probe-sidecar"]
            : ["--resource-probe-sidecar"];
        return new SidecarSpec(executable, args, AppContext.BaseDirectory);
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class ProbeExecutor : IActionExecutor
    {
        public event EventHandler<VolumeState>? VolumeChanged
        {
            add { }
            remove { }
        }

        public bool Execute(string name, object? value = null) => true;

        public VolumeState GetVolumeState() => new(50, false);

        public bool ReleaseDisplayKeepAwake() => true;

        public void Dispose()
        {
        }
    }

    /// <summary>Human-readable delta between two samples.</summary>
    private static string Drift(ResourceSample from, ResourceSample to) =>
        $"private {(to.PrivateBytes - from.PrivateBytes) / 1024.0 / 1024.0:+0.0;-0.0} MB, handles {to.Handles - from.Handles:+0;-0;+0}, GDI {to.Gdi - (long)from.Gdi:+0;-0;+0}, USER {to.User - (long)from.User:+0;-0;+0}";

    /// <summary>One settled measurement: full GC (finalizers drained so only truly-leaked native objects remain), message queue pumped, then process-wide counters.</summary>
    private static ResourceSample Sample()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Pump(250);

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new ResourceSample(
            process.PrivateMemorySize64,
            process.WorkingSet64,
            process.HandleCount,
            GetGuiResources(process.Handle, GrGdiObjects),
            GetGuiResources(process.Handle, GrUserObjects));
    }

    /// <summary>Pumps the STA message loop for at least <paramref name="milliseconds"/> (the HUD timers and BeginInvoke marshalling live on this thread; no <c>Application.Run</c> is active).</summary>
    private static void Pump(int milliseconds)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        do
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        while (Environment.TickCount64 < deadline);
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    /// <summary>One point-in-time resource measurement of this process.</summary>
    private readonly record struct ResourceSample(long PrivateBytes, long WorkingSet, int Handles, uint Gdi, uint User)
    {
        /// <inheritdoc />
        public override string ToString() =>
            $"private {PrivateBytes / 1024 / 1024.0:0.0} MB, WS {WorkingSet / 1024 / 1024.0:0.0} MB, handles {Handles}, GDI {Gdi}, USER {User}";
    }
}
