using MatterHelm.Diagnostics;
using Xunit;

namespace MatterHelm.Tests;

/// <summary>Contract checks for the hidden resource-probe sidecar route.</summary>
public sealed class ResourceProbeTests
{
    [Fact]
    public void SidecarUsesTheProductionLocalhostWebSocketHostForm()
    {
        Uri uri = ResourceProbe.SidecarUri("39531");

        Assert.Equal("ws", uri.Scheme);
        Assert.Equal("localhost", uri.Host);
        Assert.Equal(39531, uri.Port);
    }
}
