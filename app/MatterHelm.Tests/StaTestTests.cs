using MatterHelm.Ui;
using Xunit;

namespace MatterHelm.Tests;

public static class StaTestTests
{
    [Fact]
    public static void WindowMessageHandlerExceptionIsRethrownToTheTest()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            StaTest.Run(() =>
            {
                using var window = new ThrowingOnShownForm();
                window.Show();
                StaTest.PumpUntilIdle(window);
            }));

        Assert.Equal("OnShown failed", exception.Message);
    }

    [Fact]
    public static void AsyncBodyAndContinuationUseThePumpedWindowsFormsContext()
    {
        StaTest.Run(async () =>
        {
            int staThreadId = Environment.CurrentManagedThreadId;
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            Assert.IsType<WindowsFormsSynchronizationContext>(SynchronizationContext.Current);

            await Task.Yield();

            Assert.Equal(staThreadId, Environment.CurrentManagedThreadId);
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            Assert.IsType<WindowsFormsSynchronizationContext>(SynchronizationContext.Current);
        });
    }

    [Fact]
    public static void FormExistsOnlyOnThePrivateDesktopNotTheInputDesktop()
    {
        StaTest.Run(() =>
        {
            using var window = new Form { Text = "MatterHelm private desktop proof" };
            window.Show();
            StaTest.PumpUntilIdle(window);

            var proof = HeadlessDesktop.CaptureIsolationForTest(window.Handle);

            Assert.NotEqual(proof.InputDesktopName, proof.ThreadDesktopName);
            Assert.True(proof.WindowOnThreadDesktop);
            Assert.False(proof.WindowOnInputDesktop);
        });
    }

    [Fact]
    public static void TrayContextNotifyIconIsToleratedOnThePrivateDesktop()
    {
        StaTest.Run(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "MatterHelm.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var config = new Config(Path.Combine(directory, "config.json"), (_, _) => { });
                using var tray = new TrayContext(config);

                Assert.Equal("MatterHelm", tray.ToolTipText);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact]
    public static void ScreenAndWorkingAreaRemainUsableOnThePrivateDesktop()
    {
        StaTest.Run(() =>
        {
            Screen screen = Screen.PrimaryScreen
                ?? throw new InvalidOperationException("No screen is available on the private desktop.");

            Assert.True(screen.Bounds.Width > 0);
            Assert.True(screen.Bounds.Height > 0);
            Assert.True(screen.WorkingArea.Width > 0);
            Assert.True(screen.WorkingArea.Height > 0);
            Assert.True(screen.Bounds.Contains(screen.WorkingArea));
        });
    }

    private sealed class ThrowingOnShownForm : Form
    {
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            throw new InvalidOperationException("OnShown failed");
        }
    }
}
