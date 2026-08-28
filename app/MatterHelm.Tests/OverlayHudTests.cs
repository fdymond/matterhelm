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
}
