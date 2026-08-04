using System.Drawing.Imaging;

namespace HtpcMatterBridge.Demos;

/// <summary>
/// S2-4 acceptance evidence: opens <see cref="Ui.PairingWindow"/> with a
/// sample commissioning payload, lets it lay out, screenshots it to
/// <c>pairing-window-demo.png</c> next to the exe, and objectively checks
/// that the QR area actually rendered (non-trivial pixel variance, not a
/// blank box) before closing. Returns 0 iff that check passes. Invoked via
/// <c>HtpcMatterBridge.exe --demo-pairing-window</c>; not part of the
/// production tray flow.
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
    private const int MinDistinctSampledColors = 2;

    /// <summary>Runs the demo. Returns 0 iff the QR image rendered with non-trivial pixel variance.</summary>
    internal static int Run()
    {
        ApplicationConfiguration.Initialize();

        using var window = new Ui.PairingWindow();
        window.SetPairingInfo(DemoQrPayload, DemoManualCode);
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(80, 80);
        window.Show();
        window.Activate();

        DemoSupport.Pump(DemoDisplayMilliseconds);

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

        Console.WriteLine($"[{(hasVariance ? "PASS" : "FAIL")}] QR image rendered with non-trivial pixel variance.");
        Console.WriteLine($"(screenshot: {outputPath})");

        window.Close();
        return hasVariance ? 0 : 1;
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
