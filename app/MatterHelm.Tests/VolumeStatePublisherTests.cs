using MatterHelm.Actions;
using MatterHelm.Sidecar;
using Xunit;

namespace MatterHelm.Tests;

public sealed class VolumeStatePublisherTests
{
    [Fact]
    public void SeveralChangesPublishOnlyTheLatestStateAfterTheQuietGap()
    {
        var time = new ManualTimeProvider();
        var sent = new List<StateFrame>();
        using var publisher = new VolumeStatePublisher(
            () => new VolumeState(0, false),
            time,
            (_, _) => { });
        publisher.SetSender(frame =>
        {
            sent.Add(frame);
            return Task.FromResult(true);
        });

        publisher.OnVolumeChanged(null, new VolumeState(10, false));
        publisher.OnVolumeChanged(null, new VolumeState(20, true));
        publisher.OnVolumeChanged(null, new VolumeState(30, false));
        time.Advance(VolumeStatePublisher.PublishDebounce - TimeSpan.FromMilliseconds(1));
        Assert.Empty(sent);

        time.Advance(TimeSpan.FromMilliseconds(1));

        StateFrame frame = Assert.Single(sent);
        Assert.Equal(30, frame.Volume);
        Assert.False(frame.Muted);
    }

    [Fact]
    public void CommandEchoInsideTheDeadBandIsSuppressedUntilTheWindowExpires()
    {
        var time = new ManualTimeProvider();
        var sent = new List<StateFrame>();
        using var publisher = new VolumeStatePublisher(
            () => new VolumeState(0, false),
            time,
            (_, _) => { });
        publisher.SetSender(frame =>
        {
            sent.Add(frame);
            return Task.FromResult(true);
        });
        publisher.RecordCommandedState(new VolumeState(76, false));

        publisher.OnVolumeChanged(null, new VolumeState(77, false));
        time.Advance(VolumeStatePublisher.PublishDebounce);
        Assert.Empty(sent);

        time.Advance(TimeSpan.FromSeconds(3) + TimeSpan.FromMilliseconds(1));
        publisher.OnVolumeChanged(null, new VolumeState(77, false));
        time.Advance(VolumeStatePublisher.PublishDebounce);

        Assert.Single(sent);
    }

    [Fact]
    public async Task StuckSendKeepsOneInFlightAndOneOverwriteableLatestState()
    {
        var time = new ManualTimeProvider();
        var sent = new List<StateFrame>();
        var firstSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var publisher = new VolumeStatePublisher(
            () => new VolumeState(0, false),
            time,
            (_, _) => { });
        publisher.SetSender(frame =>
        {
            sent.Add(frame);
            return sent.Count == 1 ? firstSend.Task : Task.FromResult(true);
        });

        publisher.OnVolumeChanged(null, new VolumeState(10, false));
        time.Advance(VolumeStatePublisher.PublishDebounce);
        publisher.OnVolumeChanged(null, new VolumeState(20, false));
        time.Advance(VolumeStatePublisher.PublishDebounce);
        publisher.OnVolumeChanged(null, new VolumeState(30, true));
        time.Advance(VolumeStatePublisher.PublishDebounce);

        Assert.Single(sent);
        Assert.Equal(2, publisher.PendingSendCount);

        firstSend.SetResult(true);
        await TestSupport.WaitUntilAsync(
            () => sent.Count == 2,
            TimeSpan.FromSeconds(2),
            "the latest state to follow the released send");

        Assert.Equal(30, sent[1].Volume);
        Assert.True(sent[1].Muted);
        Assert.Equal(0, publisher.PendingSendCount);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        internal void Advance(TimeSpan delta)
        {
            _utcNow += delta;
            _timestamp += delta.Ticks;
            foreach (ManualTimer timer in _timers.ToArray())
            {
                timer.FireIfDue(_timestamp);
            }
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private long _dueAt = long.MaxValue;
            private bool _disposed;

            internal ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed)
                {
                    return false;
                }

                _dueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : _owner._timestamp + dueTime.Ticks;
                return true;
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            internal void FireIfDue(long now)
            {
                if (_disposed || now < _dueAt)
                {
                    return;
                }

                _dueAt = long.MaxValue;
                _callback(_state);
            }
        }
    }
}
