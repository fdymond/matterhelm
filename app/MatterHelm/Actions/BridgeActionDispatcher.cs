using MatterHelm.Sidecar;
using MatterHelm.Ui;

namespace MatterHelm.Actions;

/// <summary>
/// Dispatches protocol actions and owns the cancellable lifetime of
/// delay-bearing custom-command macros.
/// </summary>
internal sealed class BridgeActionDispatcher : IDisposable
{
    internal const int MaxConcurrentMacros = 8;
    private const int VolumeStepPercent = 5;
    private static readonly TimeSpan MacroShutdownWait = TimeSpan.FromSeconds(2);

    private readonly Config _config;
    private readonly IActionExecutor _executor;
    private readonly Action<OverlayContent>? _overlaySink;
    private readonly Action<string, string> _log;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _macroGate = new();
    private readonly Lock _sessionGate = new();
    private readonly HashSet<Task> _macroTasks = [];
    private readonly HashSet<string> _activeMacroKeys = new(StringComparer.Ordinal);
    private CancellationTokenSource _macroCts = new();
    private bool _macrosStopping = true;
    private PowerOffAction _sessionPowerOffAction;

    internal BridgeActionDispatcher(
        Config config,
        IActionExecutor executor,
        Action<OverlayContent>? overlaySink,
        Action<string, string> log,
        TimeProvider? timeProvider = null)
    {
        _config = config;
        _executor = executor;
        _overlaySink = overlaySink;
        _log = log;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sessionPowerOffAction = config.Current.PowerOffAction;
    }

    internal int RunningMacroCount
    {
        get
        {
            lock (_macroGate)
            {
                return _macroTasks.Count;
            }
        }
    }

    internal (bool Ok, string Pill, string? Error) ExecuteFrame(ActionFrame frame) => frame switch
    {
        SetVolumeFrame v => (_executor.Execute("setVolume", v.Value), $"volume set to {v.Value} %", null),
        SetMutedFrame m => (_executor.Execute("setMuted", m.Value), m.Value ? "muted" : "unmuted", null),
        BareActionFrame { Name: BareActionName.PlayPause } => (_executor.Execute("playPause"), "play/pause pressed", null),
        BareActionFrame { Name: BareActionName.Play } => (_executor.Execute("play"), "play", null),
        BareActionFrame { Name: BareActionName.Pause } => (_executor.Execute("pause"), "pause", null),
        BareActionFrame { Name: BareActionName.Next } => (_executor.Execute("next"), "next track", null),
        BareActionFrame { Name: BareActionName.Previous } => (_executor.Execute("previous"), "previous track", null),
        BareActionFrame { Name: BareActionName.PowerOn } => ExecutePowerAction(on: true),
        BareActionFrame { Name: BareActionName.PowerOff } => ExecutePowerAction(on: false),
        CustomActionFrame custom => ExecuteCustom(custom),
        _ => (false, "unknown action", null),
    };

    internal (bool Ok, string Pill, string? Error) ExecutePowerAction(bool on)
    {
        bool released = !on || _executor.ReleaseDisplayKeepAwake();
        (bool ok, string pill) = RoutePowerAction(
            GetSessionPowerOffAction(),
            on,
            _executor.Execute,
            _executor.ExecuteDisplaysOff);
        return (released & ok, pill, null);
    }

    internal static (bool Ok, string Pill) RoutePowerAction(
        PowerOffAction action,
        bool on,
        Func<string, object?, bool> execute,
        Func<DisplayPowerOffResult>? executeDisplaysOff = null)
    {
        if (on)
        {
            return action switch
            {
                PowerOffAction.DisplaysOff or PowerOffAction.PauseAndDisplaysOff =>
                    (execute("powerOn", null), "displays woken"),
                PowerOffAction.Screensaver =>
                    (execute("stopScreenSaver", null), "screensaver dismissed"),
                PowerOffAction.Sleep => (true, "no action needed"),
                _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
            };
        }

        if (action == PowerOffAction.Screensaver)
        {
            return (execute("startScreenSaver", null), "screensaver started");
        }

        if (action == PowerOffAction.Sleep)
        {
            return (execute("sleep", null), "sleeping");
        }

        if (action is not (PowerOffAction.DisplaysOff or PowerOffAction.PauseAndDisplaysOff))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }

        bool paused = action != PowerOffAction.PauseAndDisplaysOff || execute("pause", null);
        DisplayPowerOffResult displayResult = executeDisplaysOff?.Invoke()
            ?? new DisplayPowerOffResult(execute("powerOff", null), DisplayPowerOffPath.None);
        bool fallback = displayResult.Path == DisplayPowerOffPath.BlankingFallback;
        string pill = action == PowerOffAction.PauseAndDisplaysOff
            ? fallback ? "Paused + displays off — standby likely" : "paused + displays off"
            : fallback ? "Displays off — standby likely" : "displays off";
        return (paused & displayResult.Ok, pill);
    }

    internal static (bool Ok, string Pill, string? Error) RouteMouseMoveAction(
        string commandKey,
        MouseMoveActionConfig action,
        bool on,
        Func<string, object?, bool> execute)
    {
        bool moved = execute("mouseMove", new MouseMoveRequest(commandKey, action, on));
        return moved
            ? (true, on ? "mouse moved" : "mouse restored", null)
            : (false, "failed", $"could not move or restore the mouse for {commandKey} (see the app log)");
    }

    internal static (bool Ok, string Pill, string? Error) RouteMouseMoveStep(
        string commandKey,
        MouseMoveActionConfig action,
        Func<string, object?, bool> execute)
    {
        bool moved = execute("mouseMoveOnce", new MouseMoveOnceRequest(action));
        return moved
            ? (true, "mouse moved", null)
            : (false, "failed", $"could not move the mouse for {commandKey} (see the app log)");
    }

    internal static (bool Ok, string Pill, string? Error) RunSequenceSteps(
        string commandKey,
        SequenceActionConfig sequence,
        Func<CustomActionConfig, (bool Ok, string Pill, string? Error)> executeStep)
    {
        for (int i = 0; i < sequence.Steps.Count; i++)
        {
            if (sequence.Steps[i] is SequenceActionConfig)
            {
                return (false, "failed", $"custom command {commandKey}: step {i + 1} is a nested sequence");
            }

            (bool stepOk, string stepPill, string? stepError) = executeStep(sequence.Steps[i]);
            if (!stepOk)
            {
                return (false, "failed", $"custom command {commandKey}: step {i + 1} of {sequence.Steps.Count} failed: {stepError ?? stepPill}");
            }
        }

        return (true, $"ran {sequence.Steps.Count} steps", null);
    }

    internal (int? VolumePercent, bool Muted) DescribeVolumeResult(ActionFrame frame) => frame switch
    {
        SetVolumeFrame v => (v.Value, false),
        SetMutedFrame m => (ReadBackVolumePercent(), m.Value),
        CustomActionFrame custom when FindCustomCommand(custom.Key)?.Action is MediaKeyActionConfig
        {
            KeyName: MediaKeyName.VolumeUp or MediaKeyName.VolumeDown,
        } => (ReadBackVolumePercent(), false),
        _ => (null, false),
    };

    internal string DescribeIntent(ActionFrame frame) => frame switch
    {
        SetVolumeFrame v => $"volume {v.Value} %",
        SetMutedFrame m => m.Value ? "mute" : "unmute",
        BareActionFrame { Name: BareActionName.PlayPause } => "play/pause",
        BareActionFrame { Name: BareActionName.Play } => "play",
        BareActionFrame { Name: BareActionName.Pause } => "pause",
        BareActionFrame { Name: BareActionName.Next } => "next track",
        BareActionFrame { Name: BareActionName.Previous } => "previous track",
        BareActionFrame { Name: BareActionName.PowerOn } => "power on",
        BareActionFrame { Name: BareActionName.PowerOff } => GetSessionPowerOffAction() switch
        {
            PowerOffAction.DisplaysOff => "power off (→ displays off)",
            PowerOffAction.Screensaver => "power off (→ screensaver)",
            PowerOffAction.Sleep => "power off (→ sleep)",
            _ => "power off (→ pause + displays off)",
        },
        CustomActionFrame custom => FindCustomCommand(custom.Key)?.Name ?? custom.Key,
        _ => frame.GetType().Name,
    };

    internal void BeginMacroSession()
    {
        CancellationTokenSource previous;
        lock (_macroGate)
        {
            previous = _macroCts;
            _macroCts = new CancellationTokenSource();
            _macrosStopping = false;
        }

        previous.Dispose();
    }

    internal void SetSessionPowerOffAction(PowerOffAction action)
    {
        lock (_sessionGate)
        {
            _sessionPowerOffAction = action;
        }
    }

    internal Task[] CancelMacroSession()
    {
        CancellationTokenSource cancellation;
        Task[] tasks;
        lock (_macroGate)
        {
            _macrosStopping = true;
            cancellation = _macroCts;
            tasks = [.. _macroTasks];
        }

        cancellation.Cancel();
        return tasks;
    }

    internal void DrainMacroSession(Task[] tasks)
    {
        if (tasks.Length > 0 && !Task.WaitAll(tasks, MacroShutdownWait))
        {
            _log(
                "WARN",
                $"bridge: {tasks.Count(task => !task.IsCompleted)} macro task(s) did not stop within {MacroShutdownWait.TotalSeconds:0.#} s; shutdown continues.");
        }
    }

    public void Dispose() => _macroCts.Dispose();

    private (bool Ok, string Pill, string? Error) ExecuteCustom(CustomActionFrame frame)
    {
        CustomCommandConfig? command = FindCustomCommand(frame.Key);
        if (command is null)
        {
            return (false, "failed", $"unknown or disabled custom command: {frame.Key}");
        }

        return ExecuteCustomAction(frame.Key, command.Action, frame.On);
    }

    private (bool Ok, string Pill, string? Error) ExecuteCustomAction(
        string commandKey,
        CustomActionConfig action,
        bool on = true,
        bool sequenceStep = false)
    {
        switch (action)
        {
            case MediaKeyActionConfig mediaKey:
                return ExecuteMediaKey(mediaKey.KeyName);
            case LaunchActionConfig launch:
            {
                string exeName = Path.GetFileName(launch.Path);
                bool launched = _executor.Execute("launch", new LaunchRequest(launch.Path, launch.Args));
                return launched
                    ? (true, $"launched {exeName}", null)
                    : (false, "failed", $"could not start {exeName} (see the app log)");
            }
            case KeySequenceActionConfig keySequence:
                return ExecuteKeySequence(commandKey, keySequence.Sequence);
            case MouseMoveActionConfig mouseMove when sequenceStep:
                return RouteMouseMoveStep(commandKey, mouseMove, _executor.Execute);
            case MouseMoveActionConfig mouseMove:
                return RouteMouseMoveAction(commandKey, mouseMove, on, _executor.Execute);
            case SystemActionConfig system:
                return ExecuteSystemCommand(system.Command);
            case DelayActionConfig:
                return (false, "failed", $"delay is only valid inside a sequence: {commandKey}");
            case SequenceActionConfig sequence when sequence.Steps.Any(step => step is DelayActionConfig):
                return StartBackgroundSequence(commandKey, sequence);
            case SequenceActionConfig sequence:
                return RunSequenceSteps(commandKey, sequence);
            default:
                return (false, "failed", $"unsupported action type for custom command: {commandKey}");
        }
    }

    private (bool Ok, string Pill, string? Error) RunSequenceSteps(
        string commandKey,
        SequenceActionConfig sequence) =>
        RunSequenceSteps(
            commandKey,
            sequence,
            step => ExecuteCustomAction(commandKey, step, sequenceStep: true));

    private async Task<(bool Ok, string Pill, string? Error)> RunSequenceStepsAsync(
        string commandKey,
        SequenceActionConfig sequence,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < sequence.Steps.Count; i++)
        {
            CustomActionConfig step = sequence.Steps[i];
            if (step is SequenceActionConfig)
            {
                return (false, "failed", $"custom command {commandKey}: step {i + 1} is a nested sequence");
            }

            (bool stepOk, string stepPill, string? stepError) = step switch
            {
                DelayActionConfig delay => await WaitDelayStepAsync(delay.Ms, cancellationToken).ConfigureAwait(false),
                _ => ExecuteCustomAction(commandKey, step, sequenceStep: true),
            };
            if (!stepOk)
            {
                return (false, "failed", $"custom command {commandKey}: step {i + 1} of {sequence.Steps.Count} failed: {stepError ?? stepPill}");
            }
        }

        return (true, $"ran {sequence.Steps.Count} steps", null);
    }

    private async Task<(bool Ok, string Pill, string? Error)> WaitDelayStepAsync(
        int ms,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(ms), _timeProvider, cancellationToken).ConfigureAwait(false);
            return (true, $"waited {ms} ms", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (false, "failed", "macro cancelled (bridge session stopped)");
        }
    }

    private (bool Ok, string Pill, string? Error) StartBackgroundSequence(
        string commandKey,
        SequenceActionConfig sequence)
    {
        string name = FindCustomCommand(commandKey)?.Name ?? commandKey;
        Task task;
        lock (_macroGate)
        {
            if (_macrosStopping)
            {
                return (false, "failed", "macro rejected because the bridge session is stopping");
            }

            if (_activeMacroKeys.Contains(commandKey))
            {
                return (false, "failed", $"macro already running for custom command: {commandKey}");
            }

            if (_macroTasks.Count >= MaxConcurrentMacros)
            {
                return (false, "failed", $"macro capacity reached ({MaxConcurrentMacros} concurrent delay-bearing macros)");
            }

            CancellationToken cancellationToken = _macroCts.Token;
            task = Task.Run(
                () => RunBackgroundSequenceAsync(commandKey, name, sequence, cancellationToken),
                CancellationToken.None);
            _macroTasks.Add(task);
            _activeMacroKeys.Add(commandKey);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_macroGate)
                {
                    _macroTasks.Remove(completed);
                    _activeMacroKeys.Remove(commandKey);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return (true, $"running {sequence.Steps.Count} steps", null);
    }

    private async Task RunBackgroundSequenceAsync(
        string commandKey,
        string name,
        SequenceActionConfig sequence,
        CancellationToken cancellationToken)
    {
        try
        {
            (bool ok, string pill, string? error) =
                await RunSequenceStepsAsync(commandKey, sequence, cancellationToken).ConfigureAwait(false);
            _log(
                ok ? "INFO" : "ERROR",
                ok
                    ? $"bridge: macro '{commandKey}' completed ({pill})."
                    : $"bridge: macro '{commandKey}' failed: {error ?? pill}");

            if (_config.Current.OverlayEnabled)
            {
                _overlaySink?.Invoke(new OverlayContent($"Google Home → {name}", ok ? pill : "failed", !ok));
            }
        }
        catch (Exception ex)
        {
            _log("ERROR", $"bridge: macro '{commandKey}' threw: {ex.Message}");
        }
    }

    private (bool Ok, string Pill, string? Error) ExecuteKeySequence(string commandKey, string sequence)
    {
        if (!KeyChord.TryParse(sequence, out ParsedKeyChord? chord, out string? parseError))
        {
            return (false, "failed", $"invalid key sequence for custom command {commandKey}: {parseError}");
        }

        return DescribeExecution(
            _executor.ExecuteDetailed("keySequence", chord),
            $"{chord.Canonical} sent");
    }

    private (bool Ok, string Pill, string? Error) ExecuteSystemCommand(SystemCommandName command) => command switch
    {
        SystemCommandName.StartScreenSaver => ExecuteDetailed("startScreenSaver", "screensaver started"),
        SystemCommandName.StopScreenSaver => ExecuteDetailed("stopScreenSaver", "screensaver dismissed"),
        SystemCommandName.DisplaysOff => ExecuteDetailed("powerOff", "displays off"),
        SystemCommandName.DisplaysOn => ExecuteDetailed("powerOn", "displays woken"),
        SystemCommandName.Sleep => ExecuteDetailed("sleep", "sleeping"),
        SystemCommandName.Hibernate => ExecuteDetailed("hibernate", "hibernating"),
        SystemCommandName.Lock => ExecuteDetailed("lock", "workstation locked"),
        SystemCommandName.CloseForegroundProgram => ExecuteDetailed("closeForeground", "close sent to focused program"),
        SystemCommandName.Shutdown => ExecuteDetailed("shutdown", "shutting down"),
        SystemCommandName.Restart => ExecuteDetailed("restart", "restarting"),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };

    private (bool Ok, string Pill, string? Error) ExecuteMediaKey(MediaKeyName keyName) => keyName switch
    {
        MediaKeyName.PlayPause => ExecuteDetailed("playPause", "play/pause pressed"),
        MediaKeyName.Next => ExecuteDetailed("next", "next track"),
        MediaKeyName.Previous => ExecuteDetailed("previous", "previous track"),
        MediaKeyName.Stop => ExecuteDetailed("mediaStop", "stop pressed"),
        MediaKeyName.Mute => ExecuteDetailed("muteToggle", "mute toggled"),
        MediaKeyName.VolumeUp => ExecuteDetailed("volumeStep", $"volume up {VolumeStepPercent} %", VolumeStepPercent),
        MediaKeyName.VolumeDown => ExecuteDetailed("volumeStep", $"volume down {VolumeStepPercent} %", -VolumeStepPercent),
        MediaKeyName.Play => ExecuteDetailed("mediaPlay", "play pressed"),
        MediaKeyName.Pause => ExecuteDetailed("mediaPause", "pause pressed"),
        _ => throw new ArgumentOutOfRangeException(nameof(keyName), keyName, null),
    };

    private (bool Ok, string Pill, string? Error) ExecuteDetailed(
        string name,
        string successPill,
        object? value = null) =>
        DescribeExecution(_executor.ExecuteDetailed(name, value), successPill);

    private static (bool Ok, string Pill, string? Error) DescribeExecution(
        ActionExecutionResult result,
        string successPill) => result.Ok
            ? (true, successPill, null)
            : (false, "failed", result.Error ?? "action failed without a diagnostic reason");

    private int? ReadBackVolumePercent()
    {
        try
        {
            return _executor.GetVolumeState().VolumePercent;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private CustomCommandConfig? FindCustomCommand(string key) =>
        _config.Current.Commands.Custom.FirstOrDefault(c => c.Enabled && c.Key == key);

    private PowerOffAction GetSessionPowerOffAction()
    {
        lock (_sessionGate)
        {
            return _sessionPowerOffAction;
        }
    }
}
