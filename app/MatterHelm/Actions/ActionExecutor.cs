namespace MatterHelm.Actions;

/// <summary>The display-off implementation that completed an action.</summary>
public enum DisplayPowerOffPath
{
    /// <summary>No display-off path completed.</summary>
    None,

    /// <summary>At least one monitor was powered off directly through DDC/CI.</summary>
    Ddc,

    /// <summary>Windows global display blanking was used because no DDC/CI monitor succeeded.</summary>
    BlankingFallback,
}

/// <summary>Outcome of a display-off request, including the path needed for truthful UI feedback.</summary>
public readonly record struct DisplayPowerOffResult(bool Ok, DisplayPowerOffPath Path);

/// <summary>One action outcome with a truthful reason when execution failed.</summary>
public readonly record struct ActionExecutionResult(bool Ok, string? Error)
{
    /// <summary>A successful action.</summary>
    public static ActionExecutionResult Success => new(true, null);

    /// <summary>Creates a failed action with user/log-safe diagnostic detail.</summary>
    public static ActionExecutionResult Failure(string error) => new(false, error);
}

/// <summary>
/// Single dispatch point mapping protocol action names onto Windows side effects.
/// Never throws: every failure is logged and reported as <c>false</c> so the caller
/// can nack the IPC frame without the tray app crashing.
/// </summary>
public sealed class ActionExecutor : IDisposable
{
    private readonly SystemVolume _systemVolume = new();
    private readonly DisplayPower _displayPower = new();
    private readonly MouseMover _mouseMover = new();
    private readonly ScreensaverFocusMemory _screensaverFocus = new();

    /// <summary>Volume component, exposed so callers can read state and subscribe to change events.</summary>
    public SystemVolume Volume => _systemVolume;

    /// <summary>
    /// Executes the named action. <paramref name="value"/> carries the payload for value
    /// actions: <c>int</c> 0–100 for <c>setVolume</c>, <c>bool</c> for <c>setMuted</c>,
    /// <c>int</c> signed percent delta for <c>volumeStep</c>, a
    /// <see cref="LaunchRequest"/> for <c>launch</c>, a
    /// <see cref="ParsedKeyChord"/> for <c>keySequence</c>, and an internal
    /// retained mouse-move request for <c>mouseMove</c>, and a stateless
    /// request for sequence-only <c>mouseMoveOnce</c>. Beyond the protocol names,
    /// <c>play</c>/<c>pause</c> are the dedicated protocol verbs; the
    /// custom-command ops include <c>mediaStop</c>, <c>muteToggle</c>,
    /// <c>volumeStep</c>, <c>launch</c>, and <c>keySequence</c>.
    /// </summary>
    public bool Execute(string name, object? value = null)
    {
        try
        {
            switch (name)
            {
                case "playPause":
                    return MediaKeys.PlayPause();
                case "play":
                    return MediaKeys.Play();
                case "pause":
                    return MediaKeys.Pause();
                case "next":
                    return MediaKeys.NextTrack();
                case "previous":
                    return MediaKeys.PreviousTrack();
                case "mediaStop":
                    return MediaKeys.Stop();
                case "mediaPlay":
                    return MediaKeys.Play();
                case "mediaPause":
                    return MediaKeys.Pause();
                case "setVolume" when value is int percent:
                    _systemVolume.SetVolumePercent(percent);
                    return true;
                case "setMuted" when value is bool muted:
                    _systemVolume.SetMuted(muted);
                    return true;
                case "muteToggle":
                    _systemVolume.SetMuted(!_systemVolume.GetMuted());
                    return true;
                case "volumeStep" when value is int deltaPercent:
                    // SetVolumePercent clamps, so stepping past 0/100 saturates.
                    _systemVolume.SetVolumePercent(_systemVolume.GetVolumePercent() + deltaPercent);
                    return true;
                case "launch" when value is LaunchRequest request:
                    return AppLaunch.Start(request);
                case "keySequence" when value is ParsedKeyChord chord:
                    return KeyChord.Press(chord);
                case "mouseMove" when value is MouseMoveRequest request:
                    return request.On
                        ? _mouseMover.Move(request.CommandKey, request.Action)
                        : _mouseMover.Restore(request.CommandKey);
                case "mouseMoveOnce" when value is MouseMoveOnceRequest request:
                    return _mouseMover.MoveOnce(request.Action);
                // These are the display primitives; BridgeHost.RoutePowerAction
                // selects them or sleep/screensaver from the configured behavior.
                case "powerOn":
                    return _displayPower.DisplaysOn();
                case "powerOff":
                    return _displayPower.DisplaysOff();
                // S8-5 system commands (the `system` custom-action type).
                case "startScreenSaver":
                    return StartScreenSaver(_screensaverFocus, SystemCommands.StartScreenSaver);
                case "stopScreenSaver":
                    return StopScreenSaver(_screensaverFocus, SystemCommands.StopScreenSaver);
                case "lock":
                    return SystemCommands.LockWorkstation();
                case "closeForeground":
                    return SystemCommands.CloseForegroundProgram();
                case "hibernate":
                    return SystemCommands.Hibernate();
                case "shutdown":
                    return SystemCommands.Shutdown();
                case "restart":
                    return SystemCommands.Restart();
                default:
                    Log.Warn($"ActionExecutor: unknown or malformed action '{name}' (value: {value ?? "none"}).");
                    return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"ActionExecutor: action '{name}' failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Executes one action and preserves a specific failure reason for macro/ack reporting.</summary>
    public ActionExecutionResult ExecuteDetailed(string name, object? value = null)
    {
        try
        {
            return name switch
            {
                "playPause" => MediaKeys.PlayPauseDetailed(),
                "play" or "mediaPlay" => MediaKeys.PlayDetailed(),
                "pause" or "mediaPause" => MediaKeys.PauseDetailed(),
                _ => Execute(name, value)
                    ? ActionExecutionResult.Success
                    : ActionExecutionResult.Failure($"action '{name}' failed (see the app log)"),
            };
        }
        catch (Exception ex)
        {
            string error = $"action '{name}' failed: {ex.Message}";
            Log.Error($"ActionExecutor: {error}");
            return ActionExecutionResult.Failure(error);
        }
    }

    /// <summary>Executes display-off and retains the concrete path in the result.</summary>
    public DisplayPowerOffResult ExecuteDisplaysOff()
    {
        try
        {
            return _displayPower.DisplaysOffWithResult();
        }
        catch (Exception ex)
        {
            Log.Error($"ActionExecutor: action 'powerOff' failed: {ex.Message}");
            return new DisplayPowerOffResult(false, DisplayPowerOffPath.None);
        }
    }

    /// <summary>Clears the display keep-awake hold without waking the displays.</summary>
    public bool ReleaseDisplayKeepAwake() => _displayPower.ReleaseKeepAwake();

    /// <summary>Invalidates retained pointer captures for removed, disabled, or retyped commands.</summary>
    public void ReconcileMouseMoves(IReadOnlySet<string> activeCommandKeys) =>
        _mouseMover.Reconcile(activeCommandKeys);

    /// <summary>Invalidates the process-local focus capture at a bridge/app lifecycle boundary.</summary>
    public void ClearScreensaverFocusCapture() =>
        _screensaverFocus.Clear("the bridge was disabled or the app is exiting");

    internal static bool StartScreenSaver(ScreensaverFocusMemory focus, Func<bool> start)
    {
        _ = focus.Capture();
        try
        {
            bool started = start();
            if (!started)
            {
                focus.Clear("the screensaver did not start");
            }

            return started;
        }
        catch
        {
            focus.Clear("the screensaver start failed with an exception");
            throw;
        }
    }

    internal static bool StopScreenSaver(ScreensaverFocusMemory focus, Func<bool> stop)
    {
        bool stopped = stop();
        if (stopped)
        {
            // Dismissal is the action contract. Focus restoration is a
            // best-effort enhancement whose own WARN must never nack/abort it.
            _ = focus.Restore();
        }

        return stopped;
    }

    /// <summary>Disposes the volume observer and the display-power window.</summary>
    public void Dispose()
    {
        ClearScreensaverFocusCapture();
        _systemVolume.Dispose();
        _displayPower.Dispose();
    }
}
