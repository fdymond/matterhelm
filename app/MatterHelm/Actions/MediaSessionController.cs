using Windows.Media.Control;

namespace MatterHelm.Actions;

/// <summary>Outcome of asking the current Windows media session to execute an absolute verb.</summary>
internal enum MediaSessionActionResult
{
    Succeeded,
    NoCurrentSession,
    Rejected,
}

/// <summary>Testable boundary around Windows' current SMTC media session.</summary>
internal interface IMediaSessionController
{
    Task<MediaSessionActionResult> TryPlayAsync(TimeSpan timeout, CancellationToken cancellationToken);

    Task<MediaSessionActionResult> TryPauseAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Testable current-session operations owned by one acquired SMTC manager.</summary>
internal interface IMediaSessionManager
{
    Task<MediaSessionActionResult> TryPlayAsync(CancellationToken cancellationToken);

    Task<MediaSessionActionResult> TryPauseAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Executes absolute play and pause against the current Windows SMTC session.
/// The manager acquisition is cached for this process-lifetime controller;
/// the manager still resolves its current session separately for every verb.
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

    public Task<MediaSessionActionResult> TryPlayAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ExecuteAsync(static (manager, token) => manager.TryPlayAsync(token), timeout, cancellationToken);

    public Task<MediaSessionActionResult> TryPauseAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ExecuteAsync(static (manager, token) => manager.TryPauseAsync(token), timeout, cancellationToken);

    private async Task<MediaSessionActionResult> ExecuteAsync(
        Func<IMediaSessionManager, CancellationToken, Task<MediaSessionActionResult>> execute,
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
        public Task<MediaSessionActionResult> TryPlayAsync(CancellationToken cancellationToken) =>
            ExecuteCurrentAsync(static (session, token) => session.TryPlayAsync().AsTask(token), cancellationToken);

        public Task<MediaSessionActionResult> TryPauseAsync(CancellationToken cancellationToken) =>
            ExecuteCurrentAsync(static (session, token) => session.TryPauseAsync().AsTask(token), cancellationToken);

        private async Task<MediaSessionActionResult> ExecuteCurrentAsync(
            Func<GlobalSystemMediaTransportControlsSession, CancellationToken, Task<bool>> execute,
            CancellationToken cancellationToken)
        {
            GlobalSystemMediaTransportControlsSession? session = manager.GetCurrentSession();
            if (session is null)
            {
                return MediaSessionActionResult.NoCurrentSession;
            }

            bool accepted = await execute(session, cancellationToken).ConfigureAwait(false);
            return accepted ? MediaSessionActionResult.Succeeded : MediaSessionActionResult.Rejected;
        }
    }
}
