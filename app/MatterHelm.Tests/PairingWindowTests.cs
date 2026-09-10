using MatterHelm.Ui;
using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace MatterHelm.Tests;

public static class PairingWindowTests
{
    private const string SampleQrPayload = "MT:Y.K9042C00KA0648G00";

    [Fact]
    public static void ReadyToScanKeepsEssentialsVisibleAndRequirementsCollapsed()
    {
        using var window = new PairingWindow(0xFFF3, 0x801A);
        window.Show();

        window.SetPairingInfo(SampleQrPayload, "3497-011-2332");

        Assert.True(window.IdentityHelpVisible);
        Assert.True(window.QrVisible);
        Assert.True(window.ManualCodeVisible);
        Assert.Equal("3497-011-2332", window.ManualCodeText);
        Assert.Equal(
            "VID 0xFFF3 · PID 0x801A — must match your Google Developer Console integration",
            window.MatterIdentityText);
        Assert.Equal("Advertisement active · Waiting for Google Home", window.StatusText);
        Assert.DoesNotContain('\n', window.StatusText);
        Assert.True(window.SetupRequirementsLinkVisible);
        Assert.False(window.SetupRequirementsExpanded);
    }

    [Fact]
    public static void ExpandedRequirementsPreserveConsoleHubIdentityCloneAndIpv6Guidance()
    {
        using var window = new PairingWindow(0xFFF3, 0x801A);
        window.Show();
        window.SetStage(PairingStage.ReadyToScan);

        window.SetRequirementsExpanded(true);

        Assert.True(window.SetupRequirementsExpanded);
        Assert.Contains("register the VID/PID shown above", window.DeveloperConsoleHintText, StringComparison.Ordinal);
        Assert.Contains("Google Home Developer Console", window.DeveloperConsoleHintText, StringComparison.Ordinal);
        Assert.Contains("Reboot the Nest hub after any Console change", window.DeveloperConsoleHintText, StringComparison.Ordinal);
        Assert.Contains("every install needs a unique seed", window.InstallIdentityHintText, StringComparison.Ordinal);
        Assert.Contains("Never copy config.json or its seed between PCs", window.InstallIdentityHintText, StringComparison.Ordinal);
        Assert.Contains("cloned machine must mint a new seed", window.InstallIdentityHintText, StringComparison.Ordinal);
        Assert.Equal(
            "IPv6 must be enabled on any network interface used by MatterHelm.",
            window.Ipv6RequirementText);
    }

    [Fact]
    public static void DefaultIdentityMatchesTheSidecarDefaults()
    {
        string configSource = File.ReadAllText(FindRepoFile("bridge", "src", "config.ts"));
        Assert.Contains(
            "parseVendorOrProductId(env.HTPC_BRIDGE_VENDOR_ID",
            configSource,
            StringComparison.Ordinal);

        // config.ts leaves absent identity values unset; bridge.ts owns the
        // numeric fallbacks that are actually passed to matter.js.
        string bridgeSource = File.ReadAllText(FindRepoFile("bridge", "src", "matter", "bridge.ts"));
        int vendorId = ParseHexConstant(bridgeSource, "DEFAULT_VENDOR_ID");
        int productId = ParseHexConstant(bridgeSource, "DEFAULT_PRODUCT_ID");
        using var window = new PairingWindow();

        Assert.Contains(
            $"VID 0x{vendorId:X4} · PID 0x{productId:X4}",
            window.MatterIdentityText,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PairingStage.Starting)]
    [InlineData(PairingStage.Paired)]
    [InlineData(PairingStage.DiscoveryError)]
    public static void IdentityHelpIsLimitedToReadyToScan(PairingStage stage)
    {
        using var window = new PairingWindow();

        window.SetStage(stage);

        Assert.False(window.IdentityHelpVisible);
    }

    [Fact]
    public static void StageTransitionRecentersTheOpenWindowOnItsCurrentScreen()
    {
        using var window = new PairingWindow
        {
            StartPosition = FormStartPosition.Manual,
        };
        Screen screen = Screen.AllScreens.FirstOrDefault(candidate => !candidate.Primary)
            ?? Screen.PrimaryScreen
            ?? throw new InvalidOperationException("No screen is available.");
        window.Location = new Point(screen.WorkingArea.Left, screen.WorkingArea.Top);
        window.Show();

        window.SetStage(PairingStage.ReadyToScan);

        Assert.Equal(CenteredLocation(screen.WorkingArea, window.Size), window.Location);
    }

    [Fact]
    public static void FirstShowRemainsCentered()
    {
        using var window = new PairingWindow();

        window.Show();

        Screen screen = Screen.FromRectangle(window.Bounds);
        Assert.Equal(CenteredLocation(screen.WorkingArea, window.Size), window.Location);
    }

    [Fact]
    public static void FirstVisibleFrameAlreadyUsesTheSettledCenteredBounds()
    {
        using var window = new PairingWindow();
        Point? firstVisibleLocation = null;
        Size? firstVisibleSize = null;
        window.VisibleChanged += (_, _) =>
        {
            if (window.Visible && firstVisibleLocation is null)
            {
                firstVisibleLocation = window.Location;
                firstVisibleSize = window.Size;
            }
        };

        window.Show();

        Point location = Assert.IsType<Point>(firstVisibleLocation);
        Size size = Assert.IsType<Size>(firstVisibleSize);
        Screen screen = Screen.FromPoint(location);
        Assert.Equal(CenteredLocation(screen.WorkingArea, size), location);
    }

    [Fact]
    public static void FirstShowRecentersAfterTheShownLayoutSettlesToASmallerSize()
    {
        using var window = new PairingWindow();
        Size settledSize = window.Size;
        window.AutoSize = false;
        window.Size = new Size(settledSize.Width + 95, settledSize.Height);
        window.Shown += (_, _) => window.Size = settledSize;

        window.Show();

        Screen screen = Screen.FromRectangle(window.Bounds);
        Assert.Equal(CenteredLocation(screen.WorkingArea, window.Size), window.Location);
    }

    [Fact]
    public static void SameStageCodeUpdateLeavesTheWindowPositionUntouched()
    {
        using var window = new PairingWindow
        {
            StartPosition = FormStartPosition.Manual,
        };
        Screen screen = Screen.PrimaryScreen ?? throw new InvalidOperationException("No screen is available.");
        window.Show();
        window.SetPairingInfo(SampleQrPayload, "3497-011-2332");
        Point movedLocation = new(screen.WorkingArea.Left + 24, screen.WorkingArea.Top + 24);
        window.Location = movedLocation;

        window.SetPairingInfo(SampleQrPayload, "1111-222-3333");

        Assert.Equal(movedLocation, window.Location);
    }

    [Fact]
    public static void LiveCommissioningShowsPairedThenClosesAfterFourSeconds()
    {
        Action? scheduledClose = null;
        TimeSpan scheduledDelay = TimeSpan.Zero;
        using var window = new PairingWindow(
            autoCloseScheduler: (callback, delay) =>
            {
                scheduledClose = callback;
                scheduledDelay = delay;
            });
        window.Show();
        window.SetPairingInfo(SampleQrPayload, "3497-011-2332");
        Screen screen = Screen.FromRectangle(window.Bounds);
        window.Location = new Point(screen.WorkingArea.Left + 24, screen.WorkingArea.Top + 24);

        window.ShowPairedAndAutoClose();

        Assert.Equal(PairingStage.Paired, window.CurrentStage);
        Assert.Equal(CenteredLocation(screen.WorkingArea, window.Size), window.Location);
        Assert.False(window.QrVisible);
        Assert.True(window.AutoCloseScheduled);
        Assert.Contains("Closing in about 4 seconds", window.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', window.StatusText);
        Assert.Equal(TimeSpan.FromSeconds(4), scheduledDelay);

        Assert.IsType<Action>(scheduledClose)();

        Assert.True(window.IsDisposed);
    }

    [Fact]
    public static void FactoryResetCancelsPairedAutoCloseAndAcceptsAFreshCode()
    {
        Action? staleScheduledClose = null;
        using var window = new PairingWindow(
            autoCloseScheduler: (callback, _) => staleScheduledClose = callback);
        window.Show();
        window.SetPairingInfo(SampleQrPayload, "3497-011-2332");
        window.ShowPairedAndAutoClose();

        window.SetStage(PairingStage.Starting);
        window.SetPairingInfo("MT:SYNTHETIC-NOT-A-VALID-PAYLOAD", "0000-000-0000");
        Assert.IsType<Action>(staleScheduledClose)();

        Assert.False(window.IsDisposed);
        Assert.Equal(PairingStage.ReadyToScan, window.CurrentStage);
        Assert.True(window.QrVisible);
        Assert.False(window.AutoCloseScheduled);
    }

    private static int ParseHexConstant(string source, string name)
    {
        Match match = Regex.Match(
            source,
            $@"export\s+const\s+{Regex.Escape(name)}\s*=\s*0x(?<value>[0-9a-fA-F]+)\s*;");
        Assert.True(match.Success, $"Could not find exported {name} in bridge/src/matter/bridge.ts.");
        return int.Parse(match.Groups["value"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static string FindRepoFile(params string[] relativeParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine([directory.FullName, .. relativeParts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate repository file {Path.Combine(relativeParts)}.");
    }

    private static Point CenteredLocation(Rectangle workingArea, Size windowSize) => new(
        workingArea.Left + ((workingArea.Width - windowSize.Width) / 2),
        workingArea.Top + ((workingArea.Height - windowSize.Height) / 2));
}
