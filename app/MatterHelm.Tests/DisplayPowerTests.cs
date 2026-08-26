using MatterHelm.Actions;
using Xunit;

namespace MatterHelm.Tests;

public sealed class DisplayPowerTests
{
    [Fact]
    public void KeepAwakeAcquireAndReleaseUseOneOwningThreadAndAreIdempotent()
    {
        var nativeCalls = new List<(int ThreadId, uint Flags)>();
        var log = new TestSupport.LogCapture();
        using var guard = new DisplayAwakeGuard(
            flags =>
            {
                nativeCalls.Add((Environment.CurrentManagedThreadId, flags));
                return 1;
            },
            log.Sink);

        Assert.True(guard.Acquire());
        Assert.True(guard.Acquire());
        Assert.True(guard.Release());
        Assert.True(guard.Release());

        Assert.Equal(
            [DisplayAwakeGuard.EsContinuous | DisplayAwakeGuard.EsSystemRequired, DisplayAwakeGuard.EsContinuous],
            nativeCalls.Select(call => call.Flags));
        Assert.Single(nativeCalls.Select(call => call.ThreadId).Distinct());
        Assert.True(log.Contains("INFO", "keep-awake hold acquired"));
        Assert.True(log.Contains("INFO", "keep-awake hold released"));
    }

    [Fact]
    public void DisposeReleasesAnOutstandingHoldOnItsOwningThread()
    {
        var nativeCalls = new List<(int ThreadId, uint Flags)>();
        var guard = new DisplayAwakeGuard(
            flags =>
            {
                nativeCalls.Add((Environment.CurrentManagedThreadId, flags));
                return 1;
            },
            (_, _) => { });

        Assert.True(guard.Acquire());
        guard.Dispose();

        Assert.Equal(
            [DisplayAwakeGuard.EsContinuous | DisplayAwakeGuard.EsSystemRequired, DisplayAwakeGuard.EsContinuous],
            nativeCalls.Select(call => call.Flags));
        Assert.Single(nativeCalls.Select(call => call.ThreadId).Distinct());
    }

    [Fact]
    public void FailedAcquireDoesNotClaimAHoldOrAttemptARelease()
    {
        var nativeCalls = new List<uint>();
        var log = new TestSupport.LogCapture();
        using var guard = new DisplayAwakeGuard(
            flags =>
            {
                nativeCalls.Add(flags);
                return 0;
            },
            log.Sink);

        Assert.False(guard.Acquire());
        Assert.True(guard.Release());

        Assert.Equal([DisplayAwakeGuard.EsContinuous | DisplayAwakeGuard.EsSystemRequired], nativeCalls);
        Assert.True(log.Contains("ERROR", "SetThreadExecutionState"));
    }
}
