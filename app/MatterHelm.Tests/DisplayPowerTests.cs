using MatterHelm.Actions;
using Xunit;

namespace MatterHelm.Tests;

public sealed class DisplayPowerTests
{
    [Fact]
    public void DdcPowerModesUseRecoverableMccsValues()
    {
        Assert.Equal(0x01u, (uint)DdcPowerMode.On);
        Assert.Equal(0x04u, (uint)DdcPowerMode.Off);
    }

    [Theory]
    [InlineData(0, 0, (int)DisplayPowerCapability.NoDdc)]
    [InlineData(2, 0, (int)DisplayPowerCapability.AllDdc)]
    [InlineData(1, 1, (int)DisplayPowerCapability.Mixed)]
    public void CapabilityProbeClassifiesInjectedDdcMatrices(
        int supported,
        int unsupported,
        int expectedValue)
    {
        DdcMonitorPowerResult[] results =
        [
            .. Enumerable.Range(0, supported)
                .Select(i => new DdcMonitorPowerResult(new DdcMonitor($"yes-{i}", $"DDC {i}"), true)),
            .. Enumerable.Range(0, unsupported)
                .Select(i => new DdcMonitorPowerResult(new DdcMonitor($"no-{i}", $"Non-DDC {i}"), false)),
        ];
        var ddc = new FakeDdcDisplayPower([], results);

        DisplayPowerCapability capability = new DisplayPowerCapabilityProbe(ddc).Probe();

        Assert.Equal((DisplayPowerCapability)expectedValue, capability);
        Assert.Equal(1, ddc.ProbeCalls);
        Assert.Empty(ddc.Calls);
    }

    [Fact]
    public void AllDdcMonitorsUseHardwareOffAndRestoreOnlyThoseMonitors()
    {
        var tv = new DdcMonitor("tv", "Living room TV");
        var projector = new DdcMonitor("projector", "Projector");
        var ddc = new FakeDdcDisplayPower(
            new DdcMonitorPowerResult(tv, Success: true),
            new DdcMonitorPowerResult(projector, Success: true));
        var executionStateCalls = new List<uint>();
        var log = new TestSupport.LogCapture();
        int blankCalls = 0;
        int wakeCalls = 0;
        using var guard = CreateGuard(executionStateCalls, log);
        using var power = new DisplayPower(
            ddc,
            guard,
            () => { blankCalls++; return true; },
            () => { wakeCalls++; return true; },
            log.Sink);

        DisplayPowerOffResult off = power.DisplaysOffWithResult();
        Assert.True(power.DisplaysOn());

        Assert.True(off.Ok);
        Assert.Equal(DisplayPowerOffPath.Ddc, off.Path);
        Assert.Equal(0, blankCalls);
        Assert.Equal(1, wakeCalls);
        Assert.Empty(executionStateCalls);
        Assert.Equal([DdcPowerMode.Off, DdcPowerMode.On], ddc.Calls.Select(call => call.Mode));
        Assert.Null(ddc.Calls[0].Targets);
        Assert.Equal([tv, projector], ddc.Calls[1].Targets);
        Assert.True(log.Contains("INFO", "Living room TV=DDC/CI VCP 0xD6"));
        Assert.True(log.Contains("INFO", "Projector=DDC/CI VCP 0xD6"));
    }

    [Fact]
    public void NoDdcSuccessUsesGlobalBlankingWithKeepAwakeAndRestoresWithNudge()
    {
        var internalPanel = new DdcMonitor("internal", "Internal panel");
        var ddc = new FakeDdcDisplayPower(
            new DdcMonitorPowerResult(internalPanel, Success: false, "SetVCPFeature failed (50)."));
        var executionStateCalls = new List<uint>();
        var log = new TestSupport.LogCapture();
        int blankCalls = 0;
        int wakeCalls = 0;
        using var guard = CreateGuard(executionStateCalls, log);
        using var power = new DisplayPower(
            ddc,
            guard,
            () => { blankCalls++; return true; },
            () => { wakeCalls++; return true; },
            log.Sink);

        DisplayPowerOffResult off = power.DisplaysOffWithResult();
        Assert.True(power.DisplaysOn());

        Assert.True(off.Ok);
        Assert.Equal(DisplayPowerOffPath.BlankingFallback, off.Path);
        Assert.Equal(1, blankCalls);
        Assert.Equal(1, wakeCalls);
        Assert.Equal(
            [DisplayAwakeGuard.EsContinuous | DisplayAwakeGuard.EsSystemRequired, DisplayAwakeGuard.EsContinuous],
            executionStateCalls);
        Assert.Empty(ddc.Calls[1].Targets!);
        Assert.True(log.Contains("INFO", "Internal panel=SC_MONITORPOWER fallback"));
        Assert.True(log.Contains("INFO", "SC_MONITORPOWER fallback"));
        Assert.True(log.Contains("INFO", "Modern Standby may still engage"));
    }

    [Fact]
    public void BlankingFailureAfterAcquireImmediatelyReleasesTheKeepAwakeHold()
    {
        var executionStateCalls = new List<uint>();
        var log = new TestSupport.LogCapture();
        using var guard = CreateGuard(executionStateCalls, log);
        using var power = new DisplayPower(
            new FakeDdcDisplayPower(),
            guard,
            () => false,
            () => true,
            log.Sink);

        DisplayPowerOffResult result = power.DisplaysOffWithResult();

        Assert.False(result.Ok);
        Assert.Equal(DisplayPowerOffPath.None, result.Path);
        Assert.Equal(
            [DisplayAwakeGuard.EsContinuous | DisplayAwakeGuard.EsSystemRequired, DisplayAwakeGuard.EsContinuous],
            executionStateCalls);
        Assert.True(log.Contains("WARN", "blanking failed after keep-awake acquisition; the hold was released"));
    }

    [Fact]
    public void MixedDdcSupportLeavesFailedPanelOnAndDoesNotInvokeGlobalBlanking()
    {
        var tv = new DdcMonitor("tv", "Living room TV");
        var internalPanel = new DdcMonitor("internal", "Internal panel");
        var ddc = new FakeDdcDisplayPower(
            new DdcMonitorPowerResult(tv, Success: true),
            new DdcMonitorPowerResult(internalPanel, Success: false, "DDC/CI unsupported."));
        var executionStateCalls = new List<uint>();
        var log = new TestSupport.LogCapture();
        int blankCalls = 0;
        using var guard = CreateGuard(executionStateCalls, log);
        using var power = new DisplayPower(
            ddc,
            guard,
            () => { blankCalls++; return true; },
            () => true,
            log.Sink);

        Assert.True(power.DisplaysOff());
        Assert.True(power.DisplaysOn());

        Assert.Equal(0, blankCalls);
        Assert.Empty(executionStateCalls);
        Assert.Equal([tv], ddc.Calls[1].Targets);
        Assert.True(log.Contains("INFO", "Internal panel=left on"));
        Assert.True(log.Contains("INFO", "non-DDC displays=already on"));
        Assert.True(log.Contains("WARN", "global blanking can trigger Modern Standby"));
        Assert.Equal(
            2,
            log.Snapshot().Count(entry =>
                entry.Level == "INFO" && entry.Message.StartsWith("Display power ", StringComparison.Ordinal)));
    }

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

    private static DisplayAwakeGuard CreateGuard(List<uint> calls, TestSupport.LogCapture log) =>
        new(
            flags =>
            {
                calls.Add(flags);
                return 1;
            },
            log.Sink);

    private sealed class FakeDdcDisplayPower : IDdcDisplayPower
    {
        private readonly IReadOnlyList<DdcMonitorPowerResult> _offResults;
        private readonly IReadOnlyList<DdcMonitorPowerResult> _probeResults;

        internal FakeDdcDisplayPower(params DdcMonitorPowerResult[] offResults)
            : this(offResults, [])
        {
        }

        internal FakeDdcDisplayPower(
            IReadOnlyList<DdcMonitorPowerResult> offResults,
            IReadOnlyList<DdcMonitorPowerResult> probeResults)
        {
            _offResults = offResults;
            _probeResults = probeResults;
        }

        internal List<(DdcPowerMode Mode, IReadOnlyList<DdcMonitor>? Targets)> Calls { get; } = [];

        internal int ProbeCalls { get; private set; }

        public IReadOnlyList<DdcMonitorPowerResult> ProbePowerSupport()
        {
            ProbeCalls++;
            return _probeResults;
        }

        public IReadOnlyList<DdcMonitorPowerResult> SetPower(
            DdcPowerMode mode,
            IReadOnlyCollection<DdcMonitor>? targets = null)
        {
            IReadOnlyList<DdcMonitor>? targetSnapshot = targets is null ? null : [.. targets];
            Calls.Add((mode, targetSnapshot));
            return mode == DdcPowerMode.Off
                ? _offResults
                : targetSnapshot?.Select(target => new DdcMonitorPowerResult(target, Success: true)).ToArray() ?? [];
        }
    }
}
