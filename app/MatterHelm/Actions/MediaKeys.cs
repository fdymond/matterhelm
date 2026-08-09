namespace MatterHelm.Actions;

/// <summary>
/// Injects hardware media keys via <c>SendInput</c> — system-wide, the same path
/// as a physical keyboard. ADR-003: deliberately not SMTC (session-scoped, would
/// force a versioned WinRT TFM).
/// </summary>
public static class MediaKeys
{
    private const ushort VkMediaNextTrack = 0xB0;
    private const ushort VkMediaPrevTrack = 0xB1;
    private const ushort VkMediaStop = 0xB2;
    private const ushort VkMediaPlayPause = 0xB3;

    /// <summary>Sends the play/pause media key. Returns false if injection failed.</summary>
    public static bool PlayPause() => SendKey(VkMediaPlayPause, "play/pause");

    /// <summary>Sends the next-track media key. Returns false if injection failed.</summary>
    public static bool NextTrack() => SendKey(VkMediaNextTrack, "next track");

    /// <summary>Sends the previous-track media key. Returns false if injection failed.</summary>
    public static bool PreviousTrack() => SendKey(VkMediaPrevTrack, "previous track");

    /// <summary>Sends the media-stop key (S4-2 custom <c>mediaKey</c> actions). Returns false if injection failed.</summary>
    public static bool Stop() => SendKey(VkMediaStop, "stop");

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
