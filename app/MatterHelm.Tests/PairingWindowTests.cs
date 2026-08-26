using MatterHelm.Ui;
using Xunit;

namespace MatterHelm.Tests;

public static class PairingWindowTests
{
    private const string SampleQrPayload = "MT:Y.K90C0R159FZO62N10";

    [Fact]
    public static void ReadyToScanShowsTheConfiguredMatterIdentityAndRecoveryGuidance()
    {
        using var window = new PairingWindow(0xFFF3, 0x801A);

        window.SetStage(PairingStage.ReadyToScan);

        Assert.True(window.IdentityHelpVisible);
        Assert.Contains("VID 0xFFF3 · PID 0x801A", window.MatterIdentityText, StringComparison.Ordinal);
        Assert.Contains("must EXACTLY match a Matter integration", window.DeveloperConsoleHintText, StringComparison.Ordinal);
        Assert.Contains("Google Home Developer Console", window.DeveloperConsoleHintText, StringComparison.Ordinal);
        Assert.Contains("Reboot the Nest hub after any Console change", window.DeveloperConsoleHintText, StringComparison.Ordinal);
        Assert.Contains("unique per-install seed", window.InstallIdentityHintText, StringComparison.Ordinal);
        Assert.Contains("Each computer needs its own", window.InstallIdentityHintText, StringComparison.Ordinal);
        Assert.Contains("never copy config.json or the seed between PCs", window.InstallIdentityHintText, StringComparison.Ordinal);
    }

    [Fact]
    public static void DefaultIdentityMatchesTheSidecarDefaults()
    {
        using var window = new PairingWindow();

        Assert.Contains("VID 0xFFF1 · PID 0x8000", window.MatterIdentityText, StringComparison.Ordinal);
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
    public static void SameStageCodeUpdateLeavesTheWindowPositionUntouched()
    {
        using var window = new PairingWindow
        {
            StartPosition = FormStartPosition.Manual,
        };
        Screen screen = Screen.PrimaryScreen ?? throw new InvalidOperationException("No screen is available.");
        window.Show();
        window.SetPairingInfo(SampleQrPayload, "0434-914-6415");
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
        window.SetPairingInfo(SampleQrPayload, "0434-914-6415");
        Screen screen = Screen.FromRectangle(window.Bounds);
        window.Location = new Point(screen.WorkingArea.Left + 24, screen.WorkingArea.Top + 24);

        window.ShowPairedAndAutoClose();

        Assert.Equal(PairingStage.Paired, window.CurrentStage);
        Assert.Equal(CenteredLocation(screen.WorkingArea, window.Size), window.Location);
        Assert.False(window.QrVisible);
        Assert.True(window.AutoCloseScheduled);
        Assert.Contains("closes itself in about 4 seconds", window.StatusText, StringComparison.Ordinal);
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
        window.SetPairingInfo(SampleQrPayload, "0434-914-6415");
        window.ShowPairedAndAutoClose();

        window.SetStage(PairingStage.Starting);
        window.SetPairingInfo("MT:Y.K90C0R159FZO62N11", "1111-222-3333");
        Assert.IsType<Action>(staleScheduledClose)();

        Assert.False(window.IsDisposed);
        Assert.Equal(PairingStage.ReadyToScan, window.CurrentStage);
        Assert.True(window.QrVisible);
        Assert.False(window.AutoCloseScheduled);
    }

    private static Point CenteredLocation(Rectangle workingArea, Size windowSize) => new(
        workingArea.Left + ((workingArea.Width - windowSize.Width) / 2),
        workingArea.Top + ((workingArea.Height - windowSize.Height) / 2));
}
