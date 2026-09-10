using MatterHelm.Ui;
using Xunit;

namespace MatterHelm.Tests;

public sealed class SettingsCatalogTests
{
    [Fact]
    public void CatalogUsesTheSharedResetAndOpacityBounds()
    {
        SettingDescriptor reset = SettingsCatalog.Categories
            .SelectMany(category => category.Settings)
            .Single(setting => setting.Id == "momentary-reset-ms");
        SettingDescriptor opacity = SettingsCatalog.Categories
            .SelectMany(category => category.Settings)
            .Single(setting => setting.Id == "overlay-opacity");

        Assert.Equal(SettingLimits.MomentaryResetMinimumMs, reset.Minimum);
        Assert.Equal(SettingLimits.MomentaryResetMaximumMs, reset.Maximum);
        Assert.Equal(SettingLimits.OverlayOpacityMinimumPercent, opacity.Minimum);
        Assert.Equal(SettingLimits.OverlayOpacityMaximumPercent, opacity.Maximum);
    }
}
