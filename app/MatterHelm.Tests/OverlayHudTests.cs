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
    [InlineData("Google Home \u2192 Movie Mode", "Movie Mode")]
    [InlineData("Google Home -> Movie Mode", "Movie Mode")]
    [InlineData("Local preview", "Local preview")]
    public void PrimaryCommandIdentityIsRenderedInTheLowerRowWithoutARedundantSource(
        string primary,
        string expected)
    {
        var content = new OverlayContent(primary, "Executed", IsError: false);

        Assert.Equal(expected, OverlayHud.LowerRowCommandText(content));
        Assert.Equal("MatterHelm", OverlayHud.TopLineText);
    }

    [Fact]
    public void LongCommandNamesEllipsizeWithinAWidthThatDoesNotDependOnPrimary()
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
