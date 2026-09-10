using Xunit;

namespace MatterHelm.Tests;

public sealed class BridgeRestartPolicyTests
{
    [Fact]
    public void RestartMarkedConfigChangeRequiresRestart()
    {
        var before = new BridgeConfig();
        var after = new BridgeConfig { ProductId = before.ProductId + 1 };

        Assert.True(BridgeRestartPolicy.RequiresRestart(before, after));
    }

    [Fact]
    public void LiveOverlayChangeDoesNotRequireRestart()
    {
        var before = new BridgeConfig();
        var after = new BridgeConfig { OverlayOpacityPercent = 80 };

        Assert.False(BridgeRestartPolicy.RequiresRestart(before, after));
    }

    [Fact]
    public void PowerOffActionChangeRequiresSidecarRestart()
    {
        var before = new BridgeConfig();
        var after = new BridgeConfig { PowerOffAction = PowerOffAction.Sleep };

        Assert.True(BridgeRestartPolicy.RequiresRestart(before, after));
    }
}
