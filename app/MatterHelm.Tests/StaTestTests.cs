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
            Assert.IsType<WindowsFormsSynchronizationContext>(SynchronizationContext.Current);

            await Task.Yield();

            Assert.Equal(staThreadId, Environment.CurrentManagedThreadId);
            Assert.IsType<WindowsFormsSynchronizationContext>(SynchronizationContext.Current);
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
