using MatterHelm.Actions;
using MatterHelm.Diagnostics;
using MatterHelm.Sidecar;

namespace MatterHelm;

/// <summary>
/// Coalesces CoreAudio changes, suppresses command echoes, and serializes
/// state publication to the active sidecar session.
/// </summary>
internal sealed class VolumeStatePublisher : IDisposable
{
    internal static readonly TimeSpan PublishDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan EchoWindow = TimeSpan.FromSeconds(3);
    private const int EchoDeadBandPercent = 1;

    private readonly Lock _gate = new();
    private readonly Func<VolumeState> _getVolumeState;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string, string> _log;
    private ITimer? _timer;
    private Func<StateFrame, Task<bool>>? _sender;
    private VolumeState? _pendingDebounce;
    private VolumeState? _latestWhileSending;
    private int? _lastCommandedVolume;
    private bool _lastCommandedMuted;
    private long _lastVolumeCommandTimestamp;
    private bool _sendInFlight;
    private bool _disposed;

    internal VolumeStatePublisher(
        Func<VolumeState> getVolumeState,
        TimeProvider? timeProvider,
        Action<string, string> log)
    {
        _getVolumeState = getVolumeState;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = log;
    }

    internal int PendingSendCount
    {
        get
        {
            lock (_gate)
            {
                return (_sendInFlight ? 1 : 0) + (_latestWhileSending is null ? 0 : 1);
            }
        }
    }

    internal void SetSender(IpcServer? server) =>
        SetSender(server is null ? null : frame => server.SendAsync(frame));

    internal void SetSender(Func<StateFrame, Task<bool>>? sender)
    {
        lock (_gate)
        {
            _sender = sender;
        }
    }

    internal void RecordCommandedState(VolumeState state)
    {
        lock (_gate)
        {
            _lastCommandedVolume = state.VolumePercent;
            _lastCommandedMuted = state.Muted;
            _lastVolumeCommandTimestamp = _timeProvider.GetTimestamp();
        }
    }

    internal void OnVolumeChanged(object? sender, VolumeState state)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pendingDebounce = state;
            _timer ??= _timeProvider.CreateTimer(
                static state => ((VolumeStatePublisher)state!).FlushPendingPublish(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _timer.Change(PublishDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    internal void PublishSnapshot(IpcServer server)
    {
        VolumeState state;
        try
        {
            state = _getVolumeState();
        }
        catch (InvalidOperationException ex)
        {
            _log("WARN", $"bridge: cannot publish state snapshot ({ex.Message}).");
            return;
        }

        try
        {
            if (server.SendAsync(new StateFrame(state.VolumePercent, state.Muted)).GetAwaiter().GetResult())
            {
                AppMetrics.StateFramesPublished.Add(1);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // The session stopped mid-send.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sender = null;
            _pendingDebounce = null;
            _latestWhileSending = null;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void FlushPendingPublish()
    {
        VolumeState state;
        Func<StateFrame, Task<bool>> sender;
        lock (_gate)
        {
            if (_disposed || _pendingDebounce is not VolumeState pending)
            {
                return;
            }

            state = pending;
            _pendingDebounce = null;
            if (IsCommandEcho(state))
            {
                AppMetrics.StateFramesSuppressed.Add(1);
                return;
            }

            if (_sendInFlight)
            {
                _latestWhileSending = state;
                return;
            }

            if (_sender is not { } activeSender)
            {
                return;
            }

            _sendInFlight = true;
            sender = activeSender;
        }

        _ = SendLoopAsync(sender, state);
    }

    private bool IsCommandEcho(VolumeState state) =>
        _lastCommandedVolume is int commanded
        && _timeProvider.GetElapsedTime(_lastVolumeCommandTimestamp) <= EchoWindow
        && Math.Abs(state.VolumePercent - commanded) <= EchoDeadBandPercent
        && state.Muted == _lastCommandedMuted;

    private async Task SendLoopAsync(Func<StateFrame, Task<bool>> sender, VolumeState state)
    {
        while (true)
        {
            try
            {
                if (await sender(new StateFrame(state.VolumePercent, state.Muted)).ConfigureAwait(false))
                {
                    AppMetrics.StateFramesPublished.Add(1);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // The session stopped mid-send.
            }

            lock (_gate)
            {
                if (_disposed || _latestWhileSending is not VolumeState latest || _sender is not { } activeSender)
                {
                    _latestWhileSending = null;
                    _sendInFlight = false;
                    return;
                }

                state = latest;
                sender = activeSender;
                _latestWhileSending = null;
            }
        }
    }
}
