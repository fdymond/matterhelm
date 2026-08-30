using Windows.Media.Control;

namespace MatterHelm.Actions;

/// <summary>Observable playback state of a Windows media session.</summary>
internal enum MediaPlaybackState
{
    NoCurrentSession,
    Playing,
    Paused,
    Other,
}

/// <summary>A current-session observation, including the app identity that owns it.</summary>
internal sealed record MediaSessionSnapshot(MediaPlaybackState State, string? SourceAppUserModelId)
{
    internal static readonly MediaSessionSnapshot NoSession =
        new(MediaPlaybackState.NoCurrentSession, null);

    internal bool HasSession => SourceAppUserModelId is not null;
}

/// <summary>Outcome of asking one captured Windows media-session owner to execute a verb.</summary>
internal enum MediaSessionActionResult
{
    Succeeded,
    NoCurrentSession,
    TargetChanged,
    Rejected,
}

/// <summary>Testable boundary around Windows' current SMTC media session.</summary>
internal interface IMediaSessionController
{
    Task<MediaSessionSnapshot> GetCurrentSessionAsync(TimeSpan timeout, CancellationToken cancellationToken);

    Task<MediaSessionActionResult> TryPlayAsync(
        string expectedSourceAppUserModelId,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<MediaSessionActionResult> TryPauseAsync(
        string expectedSourceAppUserModelId,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>Testable current-session operations owned by one acquired SMTC manager.</summary>
internal interface IMediaSessionManager
{
    Task<MediaSessionSnapshot> GetCurrentSessionAsync(CancellationToken cancellationToken);

    Task<MediaSessionActionResult> TryPlayAsync(
        string expectedSourceAppUserModelId,
        CancellationToken cancellationToken);

    Task<MediaSessionActionResult> TryPauseAsync(
        string expectedSourceAppUserModelId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads and controls the current Windows SMTC session. Manager acquisition is
/// cached; the manager still resolves its current session for every operation.
/// Session actions are pinned to an expected source-app id so an owner change
/// between observation and fallback cannot redirect the command.
/// </summary>
internal sealed class WindowsMediaSessionController : IMediaSessionController
{
    private readonly Func<Task<IMediaSessionManager>> _requestManager;
    private readonly Lock _managerGate = new();
    private Task<IMediaSessionManager>? _managerTask;

    internal WindowsMediaSessionController()
        : this(RequestManagerAsync)
    {
    }

    internal WindowsMediaSessionController(Func<Task<IMediaSessionManager>> requestManager)
    {
        _requestManager = requestManager;
    }

    public Task<MediaSessionSnapshot> GetCurrentSessionAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ExecuteAsync(static (manager, token) => manager.GetCurrentSessionAsync(token), timeout, cancellationToken);

    public Task<MediaSessionActionResult> TryPlayAsync(
        string expectedSourceAppUserModelId,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            (manager, token) => manager.TryPlayAsync(expectedSourceAppUserModelId, token),
            timeout,
            cancellationToken);

    public Task<MediaSessionActionResult> TryPauseAsync(
        string expectedSourceAppUserModelId,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            (manager, token) => manager.TryPauseAsync(expectedSourceAppUserModelId, token),
            timeout,
            cancellationToken);

    private async Task<T> ExecuteAsync<T>(
        Func<IMediaSessionManager, CancellationToken, Task<T>> execute,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        IMediaSessionManager manager = await GetManagerTask()
            .WaitAsync(timeout, cancellationToken)
            .ConfigureAwait(false);
        return await execute(manager, cancellationToken)
            .WaitAsync(timeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<IMediaSessionManager> GetManagerTask()
    {
        lock (_managerGate)
        {
            return _managerTask ??= _requestManager();
        }
    }

    private static async Task<IMediaSessionManager> RequestManagerAsync()
    {
        GlobalSystemMediaTransportControlsSessionManager manager = await
            GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().ConfigureAwait(false);
        return new WindowsMediaSessionManager(manager);
    }

    private sealed class WindowsMediaSessionManager(
        GlobalSystemMediaTransportControlsSessionManager manager) : IMediaSessionManager
    {
        public Task<MediaSessionSnapshot> GetCurrentSessionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GlobalSystemMediaTransportControlsSession? session = manager.GetCurrentSession();
            if (session is null)
            {
                return Task.FromResult(MediaSessionSnapshot.NoSession);
            }

            MediaPlaybackState state = session.GetPlaybackInfo().PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
                _ => MediaPlaybackState.Other,
            };
            return Task.FromResult(new MediaSessionSnapshot(state, session.SourceAppUserModelId));
        }

        public Task<MediaSessionActionResult> TryPlayAsync(
            string expectedSourceAppUserModelId,
            CancellationToken cancellationToken) =>
            ExecuteCurrentAsync(
                expectedSourceAppUserModelId,
                static (session, token) => session.TryPlayAsync().AsTask(token),
                cancellationToken);

        public Task<MediaSessionActionResult> TryPauseAsync(
            string expectedSourceAppUserModelId,
            CancellationToken cancellationToken) =>
            ExecuteCurrentAsync(
                expectedSourceAppUserModelId,
                static (session, token) => session.TryPauseAsync().AsTask(token),
                cancellationToken);

        private async Task<MediaSessionActionResult> ExecuteCurrentAsync(
            string expectedSourceAppUserModelId,
            Func<GlobalSystemMediaTransportControlsSession, CancellationToken, Task<bool>> execute,
            CancellationToken cancellationToken)
        {
            GlobalSystemMediaTransportControlsSession? session = manager.GetCurrentSession();
            if (session is null)
            {
                return MediaSessionActionResult.NoCurrentSession;
            }

            if (!string.Equals(
                    session.SourceAppUserModelId,
                    expectedSourceAppUserModelId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return MediaSessionActionResult.TargetChanged;
            }

            bool accepted = await execute(session, cancellationToken).ConfigureAwait(false);
            return accepted ? MediaSessionActionResult.Succeeded : MediaSessionActionResult.Rejected;
        }
    }
}
