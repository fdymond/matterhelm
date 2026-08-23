using System.Drawing.Imaging;

namespace MatterHelm.Demos;

/// <summary>
/// S10-7 acceptance evidence: opens <see cref="Ui.WelcomeWindow"/>, screenshots
/// it beside the exe, and checks the two things that make it worth showing at
/// all — that it fits on a modest screen without clipping (it is auto-sized and
/// non-resizable, so overflow is invisible until a user hits it), and that its
/// primary button really raises <c>StartPairingRequested</c> rather than just
/// closing. Returns 0 iff both pass. Invoked via
/// <c>MatterHelm.exe --demo-welcome-window</c>; not part of the production tray
/// flow. See <see cref="PairingWindowDemo"/> for why this uses DrawToBitmap.
/// </summary>
internal static class WelcomeWindowDemo
{
    private const int DemoDisplayMilliseconds = 600;

    // The smallest height this has to survive: a 768-px laptop panel minus
    // taskbar and window chrome. Anything taller gets clipped for that user.
    // Logical (96-dpi) units — the window is measured in device pixels, and on
    // a 200 % display every device pixel of it costs half a logical one, so
    // comparing the raw heights would fail a window that fits fine.
    private const int MinSupportedWorkingHeightLogical = 700;

    /// <summary>Runs the demo. Returns 0 iff the window fits and its primary button starts pairing.</summary>
    internal static int Run()
    {
        ApplicationConfiguration.Initialize();

        bool startRequested = false;
        using var window = new Ui.WelcomeWindow();
        window.StartPairingRequested += (_, _) => startRequested = true;
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(60, 40);
        window.Show();
        window.Activate();
        DemoSupport.Pump(DemoDisplayMilliseconds);

        string outputPath = Path.Combine(AppContext.BaseDirectory, "welcome-window-demo.png");
        using (var bitmap = new Bitmap(window.Width, window.Height))
        {
            window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
            bitmap.Save(outputPath, ImageFormat.Png);
        }

        int heightBudget = window.LogicalToDeviceUnits(MinSupportedWorkingHeightLogical);
        bool fits = window.Height <= heightBudget;

        window.StartPairing();
        DemoSupport.Pump(DemoDisplayMilliseconds);

        Console.WriteLine($"[{(fits ? "PASS" : "FAIL")}] Fits a 768-px screen ({window.Height} px tall at this DPI, budget {heightBudget}).");
        Console.WriteLine($"[{(startRequested ? "PASS" : "FAIL")}] The primary button raises StartPairingRequested.");
        Console.WriteLine($"(screenshot: {outputPath})");

        return fits && startRequested ? 0 : 1;
    }
}
