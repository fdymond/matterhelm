using System.Collections.Concurrent;

namespace MatterHelm.Actions;

/// <summary>
/// Owns the thread-scoped Windows execution-state hold used while displays are
/// deliberately dark. Every native transition runs on one dedicated thread:
/// <c>SetThreadExecutionState</c> can only clear a continuous request made by
/// that same thread.
/// </summary>
internal sealed class DisplayAwakeGuard : IDisposable
{
    internal const uint EsSystemRequired = 0x00000001;
    internal const uint EsContinuous = 0x80000000;

    private readonly Func<uint, uint> _setExecutionState;
    private readonly Action<string, string> _log;
    private readonly BlockingCollection<Transition> _transitions = [];
    private readonly object _gate = new();

    private Thread? _worker;
    private bool _held;
    private bool _disposed;

    internal DisplayAwakeGuard(Func<uint, uint> setExecutionState, Action<string, string> log)
    {
        _setExecutionState = setExecutionState;
        _log = log;
    }

    /// <summary>Acquires the continuous system-required hold. Idempotent.</summary>
    internal bool Acquire()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            return _held || TransitionLocked(acquire: true);
        }
    }

    /// <summary>Clears the continuous system-required hold. Idempotent.</summary>
    internal bool Release()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return true;
            }

            return !_held || TransitionLocked(acquire: false);
        }
    }

    /// <summary>Clears any hold, then ends the owning thread.</summary>
    public void Dispose()
    {
        Thread? worker;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_held)
            {
                _ = TransitionLocked(acquire: false);
            }

            _disposed = true;
            _transitions.CompleteAdding();
            worker = _worker;
        }

        worker?.Join();
        _transitions.Dispose();
    }

    private bool TransitionLocked(bool acquire)
    {
        EnsureWorkerLocked();
        uint flags = acquire ? EsContinuous | EsSystemRequired : EsContinuous;
        var transition = new Transition(flags);
        _transitions.Add(transition);
        bool ok = transition.Completion.Task.GetAwaiter().GetResult();
        if (!ok)
        {
            return false;
        }

        _held = acquire;
        _log(
            "INFO",
            acquire
                ? "Display power: keep-awake hold acquired (ES_SYSTEM_REQUIRED)."
                : "Display power: keep-awake hold released.");
        return true;
    }

    private void EnsureWorkerLocked()
    {
        if (_worker is not null)
        {
            return;
        }

        _worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "MatterHelm display keep-awake",
        };
        _worker.Start();
    }

    private void Run()
    {
        foreach (Transition transition in _transitions.GetConsumingEnumerable())
        {
            bool ok;
            try
            {
                ok = _setExecutionState(transition.Flags) != 0;
                if (!ok)
                {
                    _log("ERROR", $"Display power: SetThreadExecutionState(0x{transition.Flags:X8}) failed.");
                }
            }
            catch (Exception ex)
            {
                _log("ERROR", $"Display power: SetThreadExecutionState failed: {ex.Message}");
                ok = false;
            }

            transition.Completion.SetResult(ok);
        }
    }

    private sealed record Transition(uint Flags)
    {
        internal TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
