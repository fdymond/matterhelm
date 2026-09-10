using System.Drawing.Imaging;

namespace MatterHelm.Demos;

/// <summary>
/// S2-4 acceptance evidence, extended for the S10-23 compact onboarding pass: walks
/// <see cref="Ui.PairingWindow"/> through all four stages with a sample
/// commissioning payload, screenshots each next to the exe, and objectively
/// checks that (a) it opens on "starting" rather than an empty code panel,
/// (b) delivering a code moves it to "ready to scan" on its own, (c) the QR
/// really rendered (non-trivial pixel variance, not a blank box), and (d) the
/// compact ready stage keeps only its essentials visible, long requirements
/// start collapsed but preserve the IPv6/Console/identity guidance, the
/// "paired" stage hides the now-unscannable code, and an advertisement
/// failure hides the misleading code and reports the error. Returns 0
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
    private const string DemoQrPayload = "MT:Y.K9042C00KA0648G00";
    private const string DemoManualCode = "3497-011-2332";
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
        bool startsOnStarting = window.StatusText.Contains("Advertisement starting", StringComparison.Ordinal);
        Capture(window, "pairing-window-demo-starting.png");

        // Delivering a code must move the window to "ready to scan" by itself.
        Screen startingScreen = Screen.FromRectangle(window.Bounds);
        window.SetPairingInfo(DemoQrPayload, DemoManualCode);
        DemoSupport.Pump(DemoDisplayMilliseconds);
        bool codeShowsWaiting = window.StatusText.Contains("Waiting for Google Home", StringComparison.Ordinal);
        bool compactEssentialsPresent = window.QrVisible
            && window.ManualCodeVisible
            && window.ManualCodeText == DemoManualCode
            && window.MatterIdentityText == "VID 0xFFF1 · PID 0x8000 — must match your Google Developer Console integration"
            && !window.StatusText.Contains('\n');
        bool requirementsStartCollapsed = window.SetupRequirementsLinkVisible
            && !window.SetupRequirementsExpanded;
        Rectangle workingArea = startingScreen.WorkingArea;
        Point expectedCenter = new(
            workingArea.Left + ((workingArea.Width - window.Width) / 2),
            workingArea.Top + ((workingArea.Height - window.Height) / 2));
        bool stageChangeCentered = window.Location == expectedCenter;

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

        window.SetRequirementsExpanded(true);
        DemoSupport.Pump(StagePumpMilliseconds);
        bool expandedRequirementsComplete = window.SetupRequirementsExpanded
            && window.DeveloperConsoleHintText.Contains("Reboot the Nest hub", StringComparison.Ordinal)
            && window.InstallIdentityHintText.Contains("cloned machine", StringComparison.Ordinal)
            && window.Ipv6RequirementText == "IPv6 must be enabled on any network interface used by MatterHelm.";
        Capture(window, "pairing-window-demo-requirements.png");
        window.SetRequirementsExpanded(false);

        // Already-commissioned: matter.js cannot mint a second code, so the
        // window must say so instead of showing a dead QR nobody can scan.
        window.SetStage(Ui.PairingStage.Paired);
        DemoSupport.Pump(StagePumpMilliseconds);
        bool pairedHidesCode = !window.QrVisible
            && window.StatusText.Contains("Paired", StringComparison.Ordinal);
        Capture(window, "pairing-window-demo-paired.png");

        window.SetStage(Ui.PairingStage.DiscoveryError);
        DemoSupport.Pump(StagePumpMilliseconds);
        bool errorHidesCode = !window.QrVisible
            && window.StatusText.Contains("Advertisement unavailable", StringComparison.Ordinal);
        Capture(window, "pairing-window-demo-discovery-error.png");

        Console.WriteLine($"[{Verdict(startsOnStarting)}] Opens on the \"starting\" stage, no blank QR.");
        Console.WriteLine($"[{Verdict(codeShowsWaiting)}] A delivered code moves the window to \"ready to scan\".");
        Console.WriteLine($"[{Verdict(compactEssentialsPresent)}] Ready stage keeps QR, manual code, identity, and one compact status line visible.");
        Console.WriteLine($"[{Verdict(requirementsStartCollapsed)}] Setup requirements are collapsed by default.");
        Console.WriteLine($"[{Verdict(expandedRequirementsComplete)}] Expanded requirements preserve Console, hub reboot, clone, and IPv6 guidance.");
        Console.WriteLine($"[{Verdict(stageChangeCentered)}] A stage change re-centers the resized window on its current screen.");
        Console.WriteLine($"[{Verdict(hasVariance)}] QR image rendered with non-trivial pixel variance.");
        Console.WriteLine($"[{Verdict(pairedHidesCode)}] The \"paired\" stage hides the code and explains why.");
        Console.WriteLine($"[{Verdict(errorHidesCode)}] Advertisement failure hides the code and reports the error.");
        Console.WriteLine($"(screenshot: {outputPath})");

        bool overall = startsOnStarting
            && codeShowsWaiting
            && compactEssentialsPresent
            && requirementsStartCollapsed
            && expandedRequirementsComplete
            && stageChangeCentered
            && hasVariance
            && pairedHidesCode
            && errorHidesCode;
        Console.WriteLine($"OVERALL: {Verdict(overall)}");
        window.Close();
        return overall ? 0 : 1;
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
