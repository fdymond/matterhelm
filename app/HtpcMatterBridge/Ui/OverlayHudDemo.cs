namespace HtpcMatterBridge.Ui;

/// <summary>
/// Story S2-3 acceptance-evidence harness. Opens a plain focusable "focus
/// sentinel" window, then drives <see cref="OverlayHud"/> through sequential
/// success/error flashes and a rapid-fire burst, sampling after every flash.
/// Asserts and records the four objective guarantees: (1) the HUD hwnd never
/// becomes the OS foreground window, (2) its ex-styles read back as exactly
/// the non-activating/click-through combination, (3) a point at its own
/// center resolves to a different window (click-through), and (4) the focus
/// sentinel never loses activation. Check (4), plus the "HUD is never this
/// thread's active window" half of check (1), use <c>GetActiveWindow</c>
/// (thread/queue-scoped) rather than relying solely on
/// <c>GetForegroundWindow</c>: on a non-interactive/automation session,
/// Windows' anti-focus-stealing lock can keep the true desktop foreground on
/// whatever the human already had focused, so this thread may never win real
/// OS foreground at all — that is orthogonal to what this story proves, and
/// <c>GetActiveWindow</c> is not subject to that lock. Invoked via
/// <c>HtpcMatterBridge.exe --demo-overlay</c>; not part of the production tray
/// flow (see the guarded branch in <c>Program.Main</c>).
/// </summary>
internal static class OverlayHudDemo
{
    private const string ResultsFileName = "overlay-hud-demo-results.txt";

    /// <summary>Runs the full demo/verification sequence. Returns 0 iff every check passed.</summary>
    internal static int Run()
    {
        var log = new List<string>();
        bool allPassed = true;

        void Check(string label, bool condition)
        {
            allPassed &= condition;
            log.Add($"[{(condition ? "PASS" : "FAIL")}] {label}");
        }

        using var sentinel = new FocusSentinelForm();
        sentinel.Show();
        sentinel.Activate();
        Pump(200);
        log.Add($"(diag) active window right after sentinel.Show()+Activate(): 0x{NativeMethods.GetActiveWindow():X} (sentinel=0x{sentinel.Handle:X})");

        using var hud = new OverlayHud { Visible = true };
        Pump(100);
        log.Add($"(diag) active window right after `new OverlayHud()`: 0x{NativeMethods.GetActiveWindow():X} (hud=0x{hud.WindowHandle:X})");

        // Baseline OS foreground window, captured once (see class doc for why
        // this — rather than "== sentinel" — is the portable gating check).
        IntPtr baselineForeground = NativeMethods.GetForegroundWindow();
        log.Add($"(diag) baseline foreground before any HUD Show(): 0x{baselineForeground:X}");

        void SampleForegroundAndHud(string label)
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            IntPtr active = NativeMethods.GetActiveWindow();
            log.Add($"    (diag) {label}: fg=0x{fg:X} active=0x{active:X} hud=0x{hud.WindowHandle:X} sentinel=0x{sentinel.Handle:X}");
            Check($"{label}: HUD hwnd is not the OS foreground window", fg != hud.WindowHandle);
            Check($"{label}: OS foreground window unchanged from baseline", fg == baselineForeground);
            Check($"{label}: HUD hwnd is not this thread's active window", active != hud.WindowHandle);
            Check($"{label}: focus sentinel is still this thread's active window", active == sentinel.Handle);
        }

        for (int i = 0; i < 3; i++)
        {
            hud.Show($"Google Home -> volume {40 + (i * 10)} %", "Volume set", isError: false);
            Pump(120);
            SampleForegroundAndHud($"flash {i} (success)");
        }

        for (int i = 0; i < 3; i++)
        {
            hud.Show("Google Home -> power off", "Failed: device unreachable", isError: true);
            Pump(120);
            SampleForegroundAndHud($"flash {i} (error)");
        }

        for (int i = 0; i < 10; i++)
        {
            hud.Show($"Google Home -> rapid cmd {i}", i % 2 == 0 ? "OK" : "Retry", isError: i % 3 == 0);
            Pump(200);
            SampleForegroundAndHud($"rapid-fire {i}");
        }

        int exStyle = unchecked((int)NativeMethods.GetWindowLongPtr(hud.WindowHandle, NativeMethods.GwlExstyle).ToInt64());
        Check("HUD ex-style has WS_EX_NOACTIVATE", (exStyle & NativeMethods.WsExNoActivate) != 0);
        Check("HUD ex-style has WS_EX_TRANSPARENT", (exStyle & NativeMethods.WsExTransparent) != 0);
        Check("HUD ex-style has WS_EX_LAYERED", (exStyle & NativeMethods.WsExLayered) != 0);

        Rectangle bounds = hud.Bounds;
        var center = new NativeMethods.Point32(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
        IntPtr atCenter = NativeMethods.WindowFromPoint(center);
        Check("WindowFromPoint(HUD center) resolves to a different window (click-through)", atCenter != hud.WindowHandle);

        // Let the hold (~2.5s) + fade (~300ms) play out fully; keep sampling.
        Pump(3200);
        SampleForegroundAndHud("post-fade");

        Check("focus sentinel recorded zero Deactivate events", !sentinel.ActivationEvents.Any(e => e.Contains("Deactivate", StringComparison.Ordinal)));

        log.Add("--- focus sentinel event log ---");
        log.AddRange(sentinel.ActivationEvents);

        string resultsPath = Path.Combine(AppContext.BaseDirectory, ResultsFileName);
        string report = string.Join(Environment.NewLine, log) + Environment.NewLine +
            $"OVERALL: {(allPassed ? "PASS" : "FAIL")}" + Environment.NewLine;
        File.WriteAllText(resultsPath, report);

        Console.WriteLine(report);
        Console.WriteLine($"(results file: {resultsPath})");

        return allPassed ? 0 : 1;
    }

    /// <summary>Pumps the STA message loop for at least <paramref name="milliseconds"/> so WinForms Timers actually fire (no Application.Run is active in this harness).</summary>
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

    /// <summary>
    /// Plain, focusable, activatable window used as a foreground/focus
    /// baseline: if the HUD ever stole activation, this window would lose it,
    /// which <see cref="ActivationEvents"/> would show as a "Deactivate" entry.
    /// </summary>
    private sealed class FocusSentinelForm : Form
    {
        internal readonly List<string> ActivationEvents = new();

        internal FocusSentinelForm()
        {
            Text = "Overlay HUD demo - focus sentinel";
            Size = new Size(360, 120);
            StartPosition = FormStartPosition.CenterScreen;
            Activated += (_, _) => ActivationEvents.Add($"{DateTime.Now:HH:mm:ss.fff} Activated");
            Deactivate += (_, _) => ActivationEvents.Add($"{DateTime.Now:HH:mm:ss.fff} Deactivate");
        }
    }
}
