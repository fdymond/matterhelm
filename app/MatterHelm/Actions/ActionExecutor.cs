namespace MatterHelm.Actions;

/// <summary>
/// Single dispatch point mapping protocol action names onto Windows side effects.
/// Never throws: every failure is logged and reported as <c>false</c> so the caller
/// can nack the IPC frame without the tray app crashing.
/// </summary>
public sealed class ActionExecutor : IDisposable
{
    private readonly SystemVolume _systemVolume = new();
    private readonly DisplayPower _displayPower = new();

    /// <summary>Volume component, exposed so callers can read state and subscribe to change events.</summary>
    public SystemVolume Volume => _systemVolume;

    /// <summary>
    /// Executes the named action. <paramref name="value"/> carries the payload for value
    /// actions: <c>int</c> 0–100 for <c>setVolume</c>, <c>bool</c> for <c>setMuted</c>,
    /// <c>int</c> signed percent delta for <c>volumeStep</c>, a
    /// <see cref="LaunchRequest"/> for <c>launch</c>, and a
    /// <see cref="ParsedKeyChord"/> for <c>keySequence</c>. Beyond the protocol names,
    /// the custom-command ops (S4-2/S7-1) are <c>mediaStop</c>, <c>muteToggle</c>,
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
                // For now power maps straight to the displays; S2-4 layers the
                // configurable powerOff behavior (displays off vs. sleep) on top.
                case "powerOn":
                    return _displayPower.DisplaysOn();
                case "powerOff":
                    return _displayPower.DisplaysOff();
                // S8-5 system commands (the `system` custom-action type).
                case "startScreenSaver":
                    return SystemCommands.StartScreenSaver();
                case "stopScreenSaver":
                    return SystemCommands.StopScreenSaver();
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

    /// <summary>Clears the display keep-awake hold without waking the displays.</summary>
    public bool ReleaseDisplayKeepAwake() => _displayPower.ReleaseKeepAwake();

    /// <summary>Disposes the volume observer and the display-power window.</summary>
    public void Dispose()
    {
        _systemVolume.Dispose();
        _displayPower.Dispose();
    }
}
