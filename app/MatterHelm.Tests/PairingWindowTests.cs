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
        StaTest.Run(() =>
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
        });
    }

    [Fact]
    public static void ExpandedRequirementsPreserveConsoleHubIdentityCloneAndIpv6Guidance()
    {
        StaTest.Run(() =>
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
        });
    }

    [Fact]
    public static void DefaultIdentityMatchesTheSidecarDefaults()
    {
        StaTest.Run(() =>
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
        });
    }

    [Theory]
    [InlineData(PairingStage.Starting)]
    [InlineData(PairingStage.Paired)]
    [InlineData(PairingStage.DiscoveryError)]
    public static void IdentityHelpIsLimitedToReadyToScan(PairingStage stage)
    {
        StaTest.Run(() =>
        {
            using var window = new PairingWindow();

            window.SetStage(stage);

            Assert.False(window.IdentityHelpVisible);
        });
    }

    [Fact]
    public static void StageTransitionRecentersTheOpenWindowOnItsCurrentScreen()
    {
        StaTest.Run(() =>
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
        });
    }

    [Fact]
    public static void FirstShowRemainsCentered()
    {
        StaTest.Run(() =>
        {
            using var window = new PairingWindow();

            window.Show();
            StaTest.PumpUntilIdle(window);

            Screen screen = Screen.FromRectangle(window.Bounds);
            Assert.Equal(CenteredLocation(screen.WorkingArea, window.Size), window.Location);
        });
    }

    [Fact]
    public static void PostShowContentSizeChangeRemainsCentered()
    {
        StaTest.Run(() =>
        {
            using var window = new PairingWindow();
            window.Show();
            StaTest.PumpUntilIdle(window);
            Size initialSize = window.Size;

            window.SetStatusTextForTest(
                "A longer live status arrived after the window was shown."
                    + Environment.NewLine
                    + "Its content-driven layout needs another line."
                    + Environment.NewLine
                    + "The final bounds must remain centered.");
            StaTest.PumpUntilIdle(window);

            Screen screen = Screen.FromRectangle(window.Bounds);
            Assert.True(window.Height > initialSize.Height);
            Assert.Equal(CenteredLocation(screen.WorkingArea, window.Size), window.Location);
        });
    }

    [Fact]
    public static void UserMoveDisablesAutomaticRecentering()
    {
        StaTest.Run(() =>
        {
            using var window = new PairingWindow();
            window.Show();
            StaTest.PumpUntilIdle(window);
            Screen screen = Screen.FromRectangle(window.Bounds);
            Point movedLocation = new(screen.WorkingArea.Left + 24, screen.WorkingArea.Top + 24);
            window.SimulateResizeBeginForTest();
            window.Location = movedLocation;
            window.SimulateResizeEndForTest();

            window.SetStatusTextForTest(
                "A longer live status arrived after the user moved this window."
                    + Environment.NewLine
                    + "Automatic sizing may continue without moving it.");
            StaTest.PumpUntilIdle(window);
            window.SetStage(PairingStage.ReadyToScan);
            StaTest.PumpUntilIdle(window);

            Assert.Equal(movedLocation, window.Location);
        });
    }

    [Fact]
    public static void TitleBarClickWithoutMovementKeepsAutomaticRecentering()
    {
        StaTest.Run(() =>
        {
            using var window = new PairingWindow();
            window.Show();
            StaTest.PumpUntilIdle(window);
            window.SimulateResizeBeginForTest();
            window.SimulateResizeEndForTest();

            window.SetStage(PairingStage.ReadyToScan);
            StaTest.PumpUntilIdle(window);

            Screen screen = Screen.FromRectangle(window.Bounds);
            Assert.Equal(CenteredLocation(screen.WorkingArea, window.Size), window.Location);
        });
    }

    [Fact]
    public static void ContentResizeInsideMoveLoopDoesNotSnapTheWindowToCenter()
    {
        StaTest.Run(() =>
        {
            using var window = new PairingWindow();
            window.Show();
            StaTest.PumpUntilIdle(window);
            Screen screen = Screen.FromRectangle(window.Bounds);
            Point dragLocation = new(screen.WorkingArea.Left + 12, screen.WorkingArea.Top + 12);
            window.Location = dragLocation;
            window.SimulateResizeBeginForTest();

            window.SetStatusTextForTest(
                "A DPI-driven content resize happened while Windows was still in the move/size loop."
                    + Environment.NewLine
                    + "The pointer must remain attached to the window.");
            StaTest.PumpUntilIdle(window);

            Assert.Equal(dragLocation, window.Location);
            window.SimulateResizeEndForTest();

            Assert.Equal(CenteredLocation(screen.WorkingArea, window.Size), window.Location);

            Size releasedSize = window.Size;
            window.SetStatusTextForTest(
                string.Join(
                    Environment.NewLine,
                    "A later content resize must preserve automatic centering.",
                    "The no-movement release did not mark this window as user positioned.",
                    "Line three forces another growth pass.",
                    "Line four keeps the expected size deterministic.",
                    "Line five is intentionally short.",
                    "Line six completes the growth fixture."));
            StaTest.PumpUntilIdle(window);

            Assert.True(window.Height > releasedSize.Height);
            Assert.Equal(CenteredLocation(screen.WorkingArea, window.Size), window.Location);
        });
    }

    [Fact]
    public static void RealMoveDuringContentResizeStillDisablesAutomaticRecentering()
    {
        StaTest.Run(() =>
        {
            using var window = new PairingWindow();
            window.Show();
            StaTest.PumpUntilIdle(window);
            int initialHeight = window.Height;
            Screen screen = Screen.FromRectangle(window.Bounds);
            Point movedLocation = new(screen.WorkingArea.Left + 24, screen.WorkingArea.Top + 24);
            window.SimulateResizeBeginForTest();
            window.Location = movedLocation;
            window.SetStatusTextForTest(
                "Content grew during a real move."
                    + Environment.NewLine
                    + "The location change must still establish user positioning.");
            StaTest.PumpUntilIdle(window);

            window.SimulateResizeEndForTest();
            int heightAfterMove = window.Height;
            window.SetStatusTextForTest(
                string.Join(
                    Environment.NewLine,
                    "Content grew again after a real move.",
                    "Automatic centering must remain disabled.",
                    "The window stays where the user released it.",
                    "Line four forces another growth pass.",
                    "Line five is intentionally short.",
                    "Line six completes the growth fixture."));
            StaTest.PumpUntilIdle(window);

            Assert.True(heightAfterMove > initialHeight);
            Assert.True(window.Height > heightAfterMove);
            Assert.Equal(movedLocation, window.Location);
        });
    }

    [Fact]
    public static void StageChangeInsideMoveLoopChangesSizeWithoutMovingTheWindow()
    {
        StaTest.Run(() =>
        {
            using var window = new PairingWindow();
            window.Show();
            StaTest.PumpUntilIdle(window);
            Screen screen = Screen.FromRectangle(window.Bounds);
            Point dragLocation = new(screen.WorkingArea.Left + 12, screen.WorkingArea.Top + 12);
            window.Location = dragLocation;
            window.SimulateResizeBeginForTest();

            window.SetStage(PairingStage.ReadyToScan);
            StaTest.PumpUntilIdle(window);

            Assert.Equal(dragLocation, window.Location);
            window.SimulateResizeEndForTest();
        });
    }

    [Fact]
    public static void UserPositionedWindowSameStageGrowthClampsToWorkingAreaWithoutRecentering()
    {
        StaTest.Run(() =>
        {
            using var window = new PairingWindow();
            window.Show();
            StaTest.PumpUntilIdle(window);
            Screen screen = Screen.FromRectangle(window.Bounds);
            Point initialBottomRight = new(
                screen.WorkingArea.Right - window.Width,
                screen.WorkingArea.Bottom - window.Height);
            window.SimulateResizeBeginForTest();
            window.Location = initialBottomRight;
            window.SimulateResizeEndForTest();
            int heightBeforeGrowth = window.Height;

            window.SetStatusTextForTest(
                "The live status grew while this user-positioned window was near the bottom edge."
                    + Environment.NewLine
                    + "The same-stage AutoSize pass must clamp it to the working area."
                    + Environment.NewLine
                    + "It must not snap back to the center.");
            StaTest.PumpUntilIdle(window);

            Assert.True(window.Height > heightBeforeGrowth);
            Point clampedBottomRight = new(
                screen.WorkingArea.Right - window.Width,
                screen.WorkingArea.Bottom - window.Height);
            Assert.Equal(clampedBottomRight, window.Location);
            Assert.NotEqual(CenteredLocation(screen.WorkingArea, window.Size), window.Location);
            Assert.True(screen.WorkingArea.Contains(window.Bounds));
        });
    }

    [Fact]
    public static void FirstVisibleFrameAlreadyUsesTheSettledCenteredBounds()
    {
        StaTest.Run(() =>
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
        });
    }

    [Fact]
    public static void FirstShowRecentersAfterTheShownLayoutSettlesToASmallerSize()
    {
        StaTest.Run(() =>
        {
            using var window = new PairingWindow();
            Size settledSize = window.Size;
            window.AutoSize = false;
            window.Size = new Size(settledSize.Width + 95, settledSize.Height);
            window.Shown += (_, _) => window.Size = settledSize;

            window.Show();

            Screen screen = Screen.FromRectangle(window.Bounds);
            Assert.Equal(CenteredLocation(screen.WorkingArea, window.Size), window.Location);
        });
    }

    [Fact]
    public static void SameStageCodeUpdateLeavesTheWindowPositionUntouched()
    {
        StaTest.Run(() =>
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
        });
    }

    [Fact]
    public static void LiveCommissioningShowsPairedThenClosesAfterFourSeconds()
    {
        StaTest.Run(() =>
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
        });
    }

    [Fact]
    public static void FactoryResetCancelsPairedAutoCloseAndAcceptsAFreshCode()
    {
        StaTest.Run(() =>
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
        });
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
