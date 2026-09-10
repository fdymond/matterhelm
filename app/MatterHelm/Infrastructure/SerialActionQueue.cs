namespace MatterHelm.Infrastructure;

/// <summary>Ordered, non-blocking dispatch for otherwise blocking operations.</summary>
internal sealed class SerialActionQueue
{
    private readonly Lock _gate = new();
    private readonly Action<Exception> _onError;
    private Task _tail = Task.CompletedTask;
    private bool _completed;

    internal SerialActionQueue(Action<Exception> onError) => _onError = onError;

    internal Task EnqueueAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            if (_completed)
            {
                return Task.CompletedTask;
            }

            _tail = _tail.ContinueWith(
                _ => Execute(action),
                CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach,
                TaskScheduler.Default);
            return _tail;
        }
    }

    internal void Complete()
    {
        Task tail;
        lock (_gate)
        {
            _completed = true;
            tail = _tail;
        }

        tail.GetAwaiter().GetResult();
    }

    private void Execute(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _onError(ex);
        }
    }
}
