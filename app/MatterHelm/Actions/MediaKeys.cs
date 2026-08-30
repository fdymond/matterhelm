namespace MatterHelm.Actions;

/// <summary>
/// Controls media through Windows' system-wide hardware-key and media-session paths.
///
/// <para>Dedicated <see cref="Play()"/>/<see cref="Pause()"/> use the current SMTC
/// session because measured <c>WM_APPCOMMAND</c> play/pause behavior can toggle.
/// No appcommand fallback is used for absolute verbs because it can invert the
/// requested intent. Next, previous, stop, and play/pause use media keys.</para>
/// </summary>
public static class MediaKeys
{
    private const ushort VkMediaNextTrack = 0xB0;
    private const ushort VkMediaPrevTrack = 0xB1;
    private const ushort VkMediaStop = 0xB2;
    private const ushort VkMediaPlayPause = 0xB3;

    private static readonly TimeSpan MediaSessionTimeout = TimeSpan.FromSeconds(2);
    private static readonly IMediaSessionController SessionController = new WindowsMediaSessionController();

    /// <summary>Sends the play/pause media key. Returns false if injection failed.</summary>
    public static bool PlayPause() => SendKey(VkMediaPlayPause, "play/pause");

    /// <summary>Sends the next-track media key. Returns false if injection failed.</summary>
    public static bool NextTrack() => SendKey(VkMediaNextTrack, "next track");

    /// <summary>Sends the previous-track media key. Returns false if injection failed.</summary>
    public static bool PreviousTrack() => SendKey(VkMediaPrevTrack, "previous track");

    /// <summary>Sends the media-stop key (S4-2 custom <c>mediaKey</c> actions). Returns false if injection failed.</summary>
    public static bool Stop() => SendKey(VkMediaStop, "stop");

    /// <summary>Requests absolute play through the current media session.</summary>
    public static bool Play() => Play(SessionController, MediaSessionTimeout, WriteLog);

    /// <summary>Requests absolute pause through the current media session.</summary>
    public static bool Pause() => Pause(SessionController, MediaSessionTimeout, WriteLog);

    internal static bool Play(
        IMediaSessionController controller,
        TimeSpan timeout,
        Action<string, string> log) =>
        SendAbsolute("play", token => controller.TryPlayAsync(timeout, token), timeout, log);

    internal static bool Pause(
        IMediaSessionController controller,
        TimeSpan timeout,
        Action<string, string> log) =>
        SendAbsolute("pause", token => controller.TryPauseAsync(timeout, token), timeout, log);

    private static bool SendAbsolute(
        string verb,
        Func<CancellationToken, Task<MediaSessionActionResult>> execute,
        TimeSpan timeout,
        Action<string, string> log)
    {
        string failureReason;
        try
        {
            using var timeoutSource = new CancellationTokenSource(timeout);
            MediaSessionActionResult result = execute(timeoutSource.Token)
                .WaitAsync(timeout, timeoutSource.Token)
                .GetAwaiter()
                .GetResult();
            if (result == MediaSessionActionResult.Succeeded)
            {
                log("DEBUG", $"media absolute '{verb}': current Windows media session accepted the SMTC verb.");
                return true;
            }

            failureReason = result == MediaSessionActionResult.NoCurrentSession
                ? "there is no current Windows media session"
                : "the current Windows media session rejected the SMTC verb";
        }
        catch (TimeoutException)
        {
            failureReason = $"the Windows media-session call timed out after {timeout.TotalSeconds:0.#} s";
        }
        catch (OperationCanceledException)
        {
            failureReason = $"the Windows media-session call timed out after {timeout.TotalSeconds:0.#} s";
        }
        catch (Exception ex)
        {
            failureReason = $"the Windows media-session call failed: {ex.Message}";
        }

        log(
            "WARN",
            $"media absolute '{verb}' failed: {failureReason}; no fallback was sent because Windows appcommands can toggle and invert the requested intent.");
        return false;
    }

    private static void WriteLog(string level, string message)
    {
        if (level == "DEBUG")
        {
            Log.Debug(message);
        }
        else
        {
            Log.Warn(message);
        }
    }

    private static bool SendKey(ushort virtualKey, string name)
    {
        // Key-down + key-up pair; media keys are extended keys on real keyboards.
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
