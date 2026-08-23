using System.Drawing.Imaging;

namespace MatterHelm.Demos;

/// <summary>
/// S2-4 acceptance evidence, extended for the S10-7 onboarding pass: walks
/// <see cref="Ui.PairingWindow"/> through all three stages with a sample
/// commissioning payload, screenshots each next to the exe, and objectively
/// checks that (a) it opens on "starting" rather than an empty code panel,
/// (b) delivering a code moves it to "ready to scan" on its own, (c) the QR
/// really rendered (non-trivial pixel variance, not a blank box), and (d) the
/// "paired" stage hides the now-unscannable code and explains why. Returns 0
/// iff every check passes. Invoked via
/// <c>MatterHelm.exe --demo-pairing-window</c>; not part of the production
/// tray flow.
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
internal static class PairingWindowDemo
{
    private const string DemoQrPayload = "MT:Y.K90C0R159FZO62N10";
    private const string DemoManualCode = "0434-914-6415";
    private const int DemoDisplayMilliseconds = 1_000;
    private const int StagePumpMilliseconds = 250;
    private const int MinDistinctSampledColors = 2;

    /// <summary>Runs the demo. Returns 0 iff every stage check above passes.</summary>
    internal static int Run()
    {
        ApplicationConfiguration.Initialize();

        using var window = new Ui.PairingWindow();
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(80, 80);
        window.Show();
        window.Activate();

        // S10-7: a fresh window must open on "Starting", not on a stale/blank
        // code panel — that empty-QR state was the original complaint.
        DemoSupport.Pump(StagePumpMilliseconds);
        bool startsOnStarting = window.StatusText.Contains("Starting the bridge", StringComparison.Ordinal);
        Capture(window, "pairing-window-demo-starting.png");

        // Delivering a code must move the window to "ready to scan" by itself.
        window.SetPairingInfo(DemoQrPayload, DemoManualCode);
        DemoSupport.Pump(DemoDisplayMilliseconds);
        bool codeShowsWaiting = window.StatusText.Contains("Waiting for the Google Home app", StringComparison.Ordinal);

        // Full window Size, not ClientSize: Form.DrawToBitmap renders the
        // whole window frame (title bar included), so a ClientSize-tall bitmap
        // silently cuts the bottom of the client area off (S4-5 finding — the
        // old capture chopped ~the title bar's height off the instructions).
        using var windowBitmap = new Bitmap(window.Width, window.Height);
        window.DrawToBitmap(windowBitmap, new Rectangle(Point.Empty, window.Size));

        string outputPath = Path.Combine(AppContext.BaseDirectory, "pairing-window-demo.png");
        windowBitmap.Save(outputPath, ImageFormat.Png);

        // QrImageBounds is client-relative; shift by the client area's offset
        // inside the full window (borders + caption) before sampling.
        Point clientOrigin = window.PointToScreen(Point.Empty);
        Rectangle qrBounds = window.QrImageBounds;
        qrBounds.Offset(clientOrigin.X - window.Left, clientOrigin.Y - window.Top);
        using Bitmap qrBitmap = windowBitmap.Clone(qrBounds, windowBitmap.PixelFormat);
        bool hasVariance = HasPixelVariance(qrBitmap, MinDistinctSampledColors);

        // Already-commissioned: matter.js cannot mint a second code, so the
        // window must say so instead of showing a dead QR nobody can scan.
        window.SetStage(Ui.PairingStage.Paired);
        DemoSupport.Pump(StagePumpMilliseconds);
        bool pairedHidesCode = !window.QrVisible
            && window.StatusText.Contains("Paired", StringComparison.Ordinal);
        Capture(window, "pairing-window-demo-paired.png");

        Console.WriteLine($"[{Verdict(startsOnStarting)}] Opens on the \"starting\" stage, no blank QR.");
        Console.WriteLine($"[{Verdict(codeShowsWaiting)}] A delivered code moves the window to \"ready to scan\".");
        Console.WriteLine($"[{Verdict(hasVariance)}] QR image rendered with non-trivial pixel variance.");
        Console.WriteLine($"[{Verdict(pairedHidesCode)}] The \"paired\" stage hides the code and explains why.");
        Console.WriteLine($"(screenshot: {outputPath})");

        window.Close();
        return startsOnStarting && codeShowsWaiting && hasVariance && pairedHidesCode ? 0 : 1;
    }

    private static string Verdict(bool pass) => pass ? "PASS" : "FAIL";

    /// <summary>Screenshots the whole window (frame included) beside the exe; see the class remarks on why this is <c>DrawToBitmap</c>.</summary>
    private static void Capture(Form window, string fileName)
    {
        using var bitmap = new Bitmap(window.Width, window.Height);
        window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
        bitmap.Save(Path.Combine(AppContext.BaseDirectory, fileName), ImageFormat.Png);
    }

    /// <summary>True iff a sampled grid of pixels contains at least <paramref name="minDistinctColors"/> distinct ARGB values — evidence of an actually-rendered QR rather than a blank/uniform box.</summary>
    private static bool HasPixelVariance(Bitmap bitmap, int minDistinctColors)
    {
        HashSet<int> distinctColors = [];
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
