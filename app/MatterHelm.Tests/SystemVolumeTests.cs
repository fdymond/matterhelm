using MatterHelm.Actions;
using Xunit;

namespace MatterHelm.Tests;

public sealed class SystemVolumeTests
{
    [Theory]
    [InlineData(-25, 0f)]
    [InlineData(0, 0f)]
    [InlineData(37, 0.37f)]
    [InlineData(100, 1f)]
    [InlineData(125, 1f)]
    public void SetVolumeClampsToTheCoreAudioScalarRange(int percent, float expectedScalar)
    {
        var backend = new FakeSystemVolumeBackend();
        using var volume = SystemVolume.CreateForTest(backend, queueWork: RunImmediately);

        volume.SetVolumePercent(percent);

        Assert.Equal(expectedScalar, backend.Scalar);
    }

    [Theory]
    [InlineData(-0.5f, 0)]
    [InlineData(0.004f, 0)]
    [InlineData(0.005f, 0)]
    [InlineData(0.006f, 1)]
    [InlineData(0.374f, 37)]
    [InlineData(0.376f, 38)]
    [InlineData(1.5f, 100)]
    public void GetVolumeRoundsAndClampsTheCoreAudioScalar(float scalar, int expectedPercent)
    {
        var backend = new FakeSystemVolumeBackend { Scalar = scalar };
        using var volume = SystemVolume.CreateForTest(backend, queueWork: RunImmediately);

        Assert.Equal(expectedPercent, volume.GetVolumePercent());
    }

    [Fact]
    public void MuteCanToggleInBothDirectionsWithoutTouchingARealDevice()
    {
        var backend = new FakeSystemVolumeBackend();
        using var volume = SystemVolume.CreateForTest(backend, queueWork: RunImmediately);

        volume.SetMuted(!volume.GetMuted());
        Assert.True(backend.Muted);
        volume.SetMuted(!volume.GetMuted());

        Assert.False(backend.Muted);
        Assert.Equal([true, false], backend.MuteWrites);
    }

    [Fact]
    public void CoreAudioChangeNotificationIsForwardedExactlyOnce()
    {
        var backend = new FakeSystemVolumeBackend();
        using var volume = SystemVolume.CreateForTest(backend, queueWork: RunImmediately);
        var received = new List<VolumeState>();
        volume.VolumeChanged += (_, state) => received.Add(state);

        backend.NotifyVolumeChanged(0.556f, muted: true);

        Assert.Equal(new VolumeState(56, true), Assert.Single(received));
    }

    [Fact]
    public void DefaultDeviceChangeReacquiresAndPublishesTheNewEndpointState()
    {
        var backend = new FakeSystemVolumeBackend { Scalar = 0.42f, Muted = true };
        using var volume = SystemVolume.CreateForTest(backend, queueWork: RunImmediately);
        var received = new List<VolumeState>();
        volume.VolumeChanged += (_, state) => received.Add(state);

        backend.NotifyDefaultDeviceChanged();

        Assert.Equal(2, backend.ReacquireCalls);
        Assert.Equal(new VolumeState(42, true), Assert.Single(received));
    }

    [Fact]
    public void BackendFailureDuringDefaultDeviceReacquisitionIsLoggedAndDoesNotEscapeTheCallback()
    {
        var backend = new FakeSystemVolumeBackend();
        var logs = new List<(string Level, string Message)>();
        using var volume = SystemVolume.CreateForTest(backend, (level, message) => logs.Add((level, message)), RunImmediately);
        // The real COM backend handles COMException itself and is covered by
        // inspection; this exercises the facade's defense against other failures.
        backend.ReacquireError = new InvalidOperationException("backend unavailable");

        Exception? error = Record.Exception(backend.NotifyDefaultDeviceChanged);

        Assert.Null(error);
        (string level, string message) = Assert.Single(logs);
        Assert.Equal("ERROR", level);
        Assert.Contains("Re-acquiring audio endpoint failed", message, StringComparison.Ordinal);
    }

    [Fact]
    public void QueueFailureDuringDefaultDeviceNotificationIsLoggedAndDoesNotEscapeTheCallback()
    {
        var backend = new FakeSystemVolumeBackend();
        var logs = new List<(string Level, string Message)>();
        using var volume = SystemVolume.CreateForTest(
            backend,
            (level, message) => logs.Add((level, message)),
            _ => throw new InvalidOperationException("queue unavailable"));

        Exception? error = Record.Exception(backend.NotifyDefaultDeviceChanged);

        Assert.Null(error);
        Assert.Contains(logs, entry =>
            entry.Level == "ERROR"
            && entry.Message.Contains("queue unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void TenDefaultDeviceNotificationsQueueOneReacquirePass()
    {
        var queued = new List<Action>();
        int reacquires = 0;
        var scheduler = new EndpointReacquireScheduler(
            () => reacquires++,
            queued.Add);

        for (int i = 0; i < 10; i++)
        {
            scheduler.Schedule();
        }

        Action work = Assert.Single(queued);
        work();
        Assert.Equal(1, reacquires);
    }

    [Fact]
    public void NotificationDuringReacquireRunsASecondPass()
    {
        var queued = new List<Action>();
        int reacquires = 0;
        EndpointReacquireScheduler? scheduler = null;
        scheduler = new EndpointReacquireScheduler(
            () =>
            {
                reacquires++;
                if (reacquires == 1)
                {
                    scheduler!.Schedule();
                }
            },
            queued.Add);

        scheduler.Schedule();
        Assert.Single(queued)();

        Assert.Equal(2, reacquires);
        Assert.Single(queued);
    }

    [Fact]
    public void QueueFailureReturnsSchedulerToIdle()
    {
        int queueAttempts = 0;
        var queued = new List<Action>();
        var scheduler = new EndpointReacquireScheduler(
            () => { },
            work =>
            {
                queueAttempts++;
                if (queueAttempts == 1)
                {
                    throw new InvalidOperationException("queue unavailable");
                }

                queued.Add(work);
            });

        Assert.Throws<InvalidOperationException>(scheduler.Schedule);
        scheduler.Schedule();

        Assert.Single(queued);
    }

    [Fact]
    public void ReacquireFailureReturnsSchedulerToIdleForALaterSuccessfulSchedule()
    {
        var queued = new List<Action>();
        int reacquires = 0;
        var scheduler = new EndpointReacquireScheduler(
            () =>
            {
                if (++reacquires == 1)
                {
                    throw new InvalidOperationException("endpoint unavailable");
                }
            },
            queued.Add);

        scheduler.Schedule();
        Assert.Throws<InvalidOperationException>(queued[0]);
        scheduler.Schedule();
        queued[1]();

        Assert.Equal(2, reacquires);
        Assert.Equal(2, queued.Count);
    }

    [Fact]
    public void DoubleDisposeDisposesTheBackendExactlyOnce()
    {
        var backend = new FakeSystemVolumeBackend();
        var volume = SystemVolume.CreateForTest(backend, queueWork: RunImmediately);

        volume.Dispose();
        volume.Dispose();

        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public void NotificationsAfterDisposeNeitherPublishNorReacquire()
    {
        var backend = new FakeSystemVolumeBackend();
        var volume = SystemVolume.CreateForTest(backend, queueWork: RunImmediately);
        var received = new List<VolumeState>();
        volume.VolumeChanged += (_, state) => received.Add(state);
        volume.Dispose();

        backend.NotifyVolumeChanged(0.75f, muted: true);
        backend.NotifyDefaultDeviceChanged();

        Assert.Empty(received);
        Assert.Equal(1, backend.ReacquireCalls);
    }

    [Fact]
    public void FailedReacquirePublishesNoState()
    {
        var backend = new FakeSystemVolumeBackend();
        using var volume = SystemVolume.CreateForTest(backend, queueWork: RunImmediately);
        var received = new List<VolumeState>();
        volume.VolumeChanged += (_, state) => received.Add(state);
        backend.ReacquireResult = false;

        backend.NotifyDefaultDeviceChanged();

        Assert.Equal(2, backend.ReacquireCalls);
        Assert.Empty(received);
    }

    private static void RunImmediately(Action work) => work();

    private sealed class FakeSystemVolumeBackend : ISystemVolumeBackend
    {
        internal float Scalar { get; set; }

        internal bool Muted { get; set; }

        internal List<bool> MuteWrites { get; } = [];

        internal int ReacquireCalls { get; private set; }

        internal int DisposeCalls { get; private set; }

        internal Exception? ReacquireError { get; set; }

        internal bool ReacquireResult { get; set; } = true;

        public event EventHandler<EndpointVolumeState>? VolumeChanged;

        public event EventHandler? DefaultDeviceChanged;

        public float GetVolumeScalar() => Scalar;

        public bool GetMuted() => Muted;

        public void SetVolumeScalar(float scalar) => Scalar = scalar;

        public void SetMuted(bool muted)
        {
            Muted = muted;
            MuteWrites.Add(muted);
        }

        public bool Reacquire()
        {
            ReacquireCalls++;
            if (ReacquireError is not null)
            {
                throw ReacquireError;
            }

            return ReacquireResult;
        }

        public void Dispose() => DisposeCalls++;

        internal void NotifyVolumeChanged(float scalar, bool muted) =>
            VolumeChanged?.Invoke(this, new EndpointVolumeState(scalar, muted));

        internal void NotifyDefaultDeviceChanged() => DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
    }
}
