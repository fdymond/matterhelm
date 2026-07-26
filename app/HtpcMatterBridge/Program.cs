using System.Drawing.Imaging;

namespace HtpcMatterBridge;

/// <summary>Composition root: enforces single instance, then runs the tray context.</summary>
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

        using var mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out bool createdNew);
        if (!createdNew)
        {
            // Another instance already owns the mutex; exit silently (no dialog, no log spam).
            return;
        }

        Log.Initialize();
        Log.Info("HtpcMatterBridge starting.");

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());

        Log.Info("HtpcMatterBridge exited.");
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
}
