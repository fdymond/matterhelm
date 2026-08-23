using System.Runtime.InteropServices;

namespace MatterHelm.Actions;

/// <summary>
/// Injects hardware media keys via <c>SendInput</c> — system-wide, the same path
/// as a physical keyboard. ADR-003: deliberately not SMTC (session-scoped, would
/// force a versioned WinRT TFM).
///
/// <para>Dedicated <see cref="Play"/>/<see cref="Pause"/> (S9-1): keyboards have
/// no dedicated play or pause virtual keys — only the toggle — but Windows'
/// <c>WM_APPCOMMAND</c> channel does (<c>APPCOMMAND_MEDIA_PLAY</c>/<c>_PAUSE</c>).
/// Sent to the foreground window, an unhandled appcommand bubbles through
/// <c>DefWindowProc</c> into the shell hook chain — the same global routing the
/// hardware media keys use — so SMTC-aware players honor the absolute verb
/// regardless of focus.</para>
/// </summary>
public static partial class MediaKeys
{
    private const ushort VkMediaNextTrack = 0xB0;
    private const ushort VkMediaPrevTrack = 0xB1;
    private const ushort VkMediaStop = 0xB2;
    private const ushort VkMediaPlayPause = 0xB3;

    private const uint WmAppCommand = 0x0319;
    private const int AppCommandMediaPlay = 46;
    private const int AppCommandMediaPause = 47;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint TimeoutMilliseconds = 1000;

    /// <summary>Sends the play/pause media key. Returns false if injection failed.</summary>
    public static bool PlayPause() => SendKey(VkMediaPlayPause, "play/pause");

    /// <summary>Sends the next-track media key. Returns false if injection failed.</summary>
    public static bool NextTrack() => SendKey(VkMediaNextTrack, "next track");

    /// <summary>Sends the previous-track media key. Returns false if injection failed.</summary>
    public static bool PreviousTrack() => SendKey(VkMediaPrevTrack, "previous track");

    /// <summary>Sends the media-stop key (S4-2 custom <c>mediaKey</c> actions). Returns false if injection failed.</summary>
    public static bool Stop() => SendKey(VkMediaStop, "stop");

    /// <summary>Sends the dedicated (absolute) play appcommand — starts playback, no toggle. Returns false if there is no window to route through.</summary>
    public static bool Play() => SendAppCommand(AppCommandMediaPlay, "play");

    /// <summary>Sends the dedicated (absolute) pause appcommand — pauses playback, no toggle. Returns false if there is no window to route through.</summary>
    public static bool Pause() => SendAppCommand(AppCommandMediaPause, "pause");

    private static bool SendAppCommand(int command, string name)
    {
        nint window = GetForegroundWindow();
        if (window == 0)
        {
            Log.Error($"media appcommand '{name}': no foreground window to route through.");
            return false;
        }

        // S9-6 review fix: a synchronous SendMessage to a HUNG foreground
        // window would block this thread — which is the IPC receive loop —
        // indefinitely (same head-of-line class as the S8-6 macro bug).
        // SMTO_ABORTIFHUNG bails immediately on a hung target; the 1 s
        // timeout bounds a merely-slow one. lParam upper word carries the
        // command (APPCOMMAND wire format).
        nint sendResult = SendMessageTimeoutW(
            window, WmAppCommand, window, (nint)command << 16,
            SmtoAbortIfHung, TimeoutMilliseconds, out _);
        if (sendResult == 0)
        {
            Log.Error($"media appcommand '{name}': the foreground window did not accept the message (hung or timed out).");
            return false;
        }

        return true;
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

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageTimeoutW(
        nint hWnd, uint msg, nint wParam, nint lParam, uint flags, uint timeoutMs, out nint result);
}
