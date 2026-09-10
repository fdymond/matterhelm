using MatterHelm.Ui;
using Xunit;

namespace MatterHelm.Tests;

public sealed class OverlayHudTests
{
    [Fact]
    public void TopLineTextIsStaticProductName()
    {
        Assert.Equal("MatterHelm", OverlayHud.TopLineText);
    }

    [Theory]
    [InlineData("Google Home \u2192 play/pause", "media key sent", "Play/Pause")]
    [InlineData("Google Home -> next track", "media key sent", "Next")]
    [InlineData("Google Home \u2192 previous track", "media key sent", "Previous")]
    [InlineData("Google Home -> power on", "display restored", "Power On")]
    [InlineData("Google Home -> power off (\u2192 displays off)", "displays off", "Power Off")]
    [InlineData("Google Home \u2192 Movie Mode", "launched kodi.exe", "Movie Mode")]
    public void SuccessfulCommandPillCarriesCommandIdentityInsteadOfResult(
        string primary,
        string result,
        string expected)
    {
        var content = new OverlayContent(primary, result, IsError: false);

        Assert.Equal(expected, OverlayHud.DisplayedPillText(content));
        Assert.Equal("MatterHelm", OverlayHud.TopLineText);
    }

    [Theory]
    [InlineData("Google Home \u2192 Volume", false, 40, "40 %")]
    [InlineData("Google Home \u2192 mute", true, 40, "Muted")]
    [InlineData("Google Home \u2192 unmute", false, 40, "Unmuted")]
    public void SpeakerPillCarriesVolumeOrMuteState(
        string primary,
        bool muted,
        int volumePercent,
        string expected)
    {
        var content = new OverlayContent(primary, "producer result is not shown", IsError: false)
        {
            VolumePercent = volumePercent,
            Muted = muted,
        };

        Assert.Equal(expected, OverlayHud.DisplayedPillText(content));
    }

    [Fact]
    public void ErrorPillRetainsExistingResultText()
    {
        var content = new OverlayContent("Google Home \u2192 Power off", "failed", IsError: true);

        Assert.Equal("failed", OverlayHud.DisplayedPillText(content));
    }

    [Fact]
    public void BlankingFallbackPillRetainsTheStandbyWarning()
    {
        var content = new OverlayContent(
            "Google Home → power off (→ displays off)",
            "Displays off — standby likely",
            IsError: false);

        Assert.Equal("Displays off — standby likely", OverlayHud.DisplayedPillText(content));
    }

    [Fact]
    public void LongCommandPillsEllipsizeWithinAWidthThatDoesNotDependOnPrimary()
    {
        var shortCommand = new OverlayContent("Google Home \u2192 Movie Mode", "Executed", IsError: false);
        var longCommand = new OverlayContent(
            "Google Home \u2192 An intentionally extremely long custom command name that cannot fit in the lower row",
            "Executed",
            IsError: false);

        Assert.Equal(
            OverlayHud.MeasureDesiredCanvasWidthForTest(shortCommand),
            OverlayHud.MeasureDesiredCanvasWidthForTest(longCommand));
    }

    [Theory]
    [InlineData(CanvasFailure.CreateCompatibleDc, 0, 0)]
    [InlineData(CanvasFailure.CreateDibSection, 0, 1)]
    [InlineData(CanvasFailure.SelectObject, 1, 1)]
    public void LayeredCanvasCreationFailureReleasesEveryAcquiredNativeResource(
        CanvasFailure failure,
        int expectedDeletedObjects,
        int expectedDeletedDcs)
    {
        var native = new FailingLayeredCanvasNative(failure);

        Assert.Throws<InvalidOperationException>(() =>
            OverlayHud.CreateLayeredCanvasForTest(32, 16, native));

        Assert.Equal(expectedDeletedObjects, native.DeletedObjects);
        Assert.Equal(expectedDeletedDcs, native.DeletedDcs);
        Assert.Equal(0, native.RestoredSelections);
    }

    public enum CanvasFailure
    {
        CreateCompatibleDc,
        CreateDibSection,
        SelectObject,
    }

    private sealed class FailingLayeredCanvasNative(CanvasFailure failure) : ILayeredCanvasNative
    {
        public int DeletedObjects { get; private set; }

        public int DeletedDcs { get; private set; }

        public int RestoredSelections { get; private set; }

        public IntPtr CreateCompatibleDc() =>
            failure == CanvasFailure.CreateCompatibleDc ? IntPtr.Zero : (IntPtr)1;

        public IntPtr CreateDibSection(
            IntPtr deviceContext,
            ref NativeMethods.BitmapInfoHeader header,
            out IntPtr bits)
        {
            bits = failure == CanvasFailure.CreateDibSection ? IntPtr.Zero : (IntPtr)3;
            return failure == CanvasFailure.CreateDibSection ? IntPtr.Zero : (IntPtr)2;
        }

        public IntPtr SelectObject(IntPtr deviceContext, IntPtr value)
        {
            if (value == (IntPtr)2)
            {
                return failure == CanvasFailure.SelectObject ? IntPtr.Zero : (IntPtr)4;
            }

            RestoredSelections++;
            return (IntPtr)2;
        }

        public void DeleteObject(IntPtr value) => DeletedObjects++;

        public void DeleteDc(IntPtr deviceContext) => DeletedDcs++;
    }
}
