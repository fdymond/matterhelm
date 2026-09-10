namespace MatterHelm.Actions;

/// <summary>Focused-first media-key actions exposed to the action executor.</summary>
public static class MediaKeys
{
    private const ushort VkMediaNextTrack = 0xB0;
    private const ushort VkMediaPrevTrack = 0xB1;
    private const ushort VkMediaStop = 0xB2;

    private static readonly IMediaSessionController SessionController = new WindowsMediaSessionController();
    private static readonly IForegroundMediaCommandSender ForegroundSender = new WindowsForegroundMediaCommandSender();
    private static readonly IMediaVerificationDelay Delay = new TaskMediaVerificationDelay();
    private static readonly UnverifiableMediaRepeatGuard RepeatGuard = new();

    /// <summary>Routes play/pause to the focused app, then an ownership-pinned absolute session fallback.</summary>
    public static bool PlayPause() => RouteDefault(ForegroundMediaCommand.PlayPause);

    /// <summary>Routes play/pause and preserves a specific failure reason.</summary>
    public static ActionExecutionResult PlayPauseDetailed() => RouteDefaultDetailed(ForegroundMediaCommand.PlayPause);

    /// <summary>Sends the next-track media key. Returns false if injection failed.</summary>
    public static bool NextTrack()
    {
        RepeatGuard.Reset();
        return SendKey(VkMediaNextTrack, "next track");
    }

    /// <summary>Sends the previous-track media key. Returns false if injection failed.</summary>
    public static bool PreviousTrack()
    {
        RepeatGuard.Reset();
        return SendKey(VkMediaPrevTrack, "previous track");
    }

    /// <summary>Sends the media-stop key. Returns false if injection failed.</summary>
    public static bool Stop()
    {
        RepeatGuard.Reset();
        return SendKey(VkMediaStop, "stop");
    }

    /// <summary>Routes absolute play to the focused app, then an ownership-pinned session fallback.</summary>
    public static bool Play() => RouteDefault(ForegroundMediaCommand.Play);

    /// <summary>Routes absolute play and preserves a specific failure reason.</summary>
    public static ActionExecutionResult PlayDetailed() => RouteDefaultDetailed(ForegroundMediaCommand.Play);

    /// <summary>Routes absolute pause to the focused app, then an ownership-pinned session fallback.</summary>
    public static bool Pause() => RouteDefault(ForegroundMediaCommand.Pause);

    /// <summary>Routes absolute pause and preserves a specific failure reason.</summary>
    public static ActionExecutionResult PauseDetailed() => RouteDefaultDetailed(ForegroundMediaCommand.Pause);

    private static bool RouteDefault(ForegroundMediaCommand command) => RouteDefaultDetailed(command).Ok;

    private static ActionExecutionResult RouteDefaultDetailed(ForegroundMediaCommand command) =>
        FocusedMediaRouter.RouteDetailed(
            command,
            SessionController,
            ForegroundSender,
            Delay,
            RepeatGuard,
            FocusedMediaRouter.ProductionRoutingPolicy,
            WriteRouteLog);

    internal static LogLevel RouteLogLevel(string level) => level switch
    {
        "DEBUG" => LogLevel.Debug,
        "ERROR" => LogLevel.Error,
        _ => LogLevel.Warn,
    };

    private static void WriteRouteLog(string level, string message)
    {
        switch (RouteLogLevel(level))
        {
            case LogLevel.Debug:
                Log.Debug(message);
                break;
            case LogLevel.Error:
                Log.Error(message);
                break;
            default:
                Log.Warn(message);
                break;
        }
    }

    private static bool SendKey(ushort virtualKey, string name)
    {
        var inputs = new NativeInput.Input[2];
        for (int i = 0; i < inputs.Length; i++)
        {
            inputs[i].Type = NativeInput.InputKeyboard;
            inputs[i].Union.Keyboard.VirtualKey = virtualKey;
            inputs[i].Union.Keyboard.Flags = NativeInput.KeyEventFExtendedKey;
        }

        inputs[1].Union.Keyboard.Flags |= NativeInput.KeyEventFKeyUp;
        return NativeInput.Send(inputs, $"media key '{name}'");
    }
}
