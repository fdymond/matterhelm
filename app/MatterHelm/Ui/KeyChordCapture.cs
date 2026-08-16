using System.Diagnostics.CodeAnalysis;
using MatterHelm.Actions;

namespace MatterHelm.Ui;

/// <summary>
/// Shared key-sequence capture logic for the command and sequence-step
/// dialogs (S7-1, factored out in S8-3): maps a WinForms key message onto the
/// <see cref="KeyChord"/> table and assembles the canonical chord from the
/// live modifier state. The arming/disarming UX (toggle button, Esc, pure
/// modifiers swallowed) stays in each dialog's <c>ProcessCmdKey</c>.
/// </summary>
internal static class KeyChordCapture
{
    /// <summary>True when <paramref name="keyCode"/> is a bare modifier press (hold it and press the main key).</summary>
    public static bool IsPureModifier(Keys keyCode) =>
        keyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin;

    /// <summary>
    /// Canonical chord for a captured non-modifier key plus the modifiers in
    /// <paramref name="keyData"/>; false = the key is outside the curated
    /// <see cref="KeyChord"/> capture set (the caller keeps capture armed).
    /// Win+ chords cannot be captured (the OS intercepts most of them).
    /// </summary>
    public static bool TryCapture(Keys keyData, [NotNullWhen(true)] out string? canonical)
    {
        canonical = null;
        if (!TryMapCapturedKey(keyData & Keys.KeyCode, out string keyName))
        {
            return false;
        }

        var modifiers = KeyChordModifiers.None;
        if (keyData.HasFlag(Keys.Control))
        {
            modifiers |= KeyChordModifiers.Ctrl;
        }

        if (keyData.HasFlag(Keys.Alt))
        {
            modifiers |= KeyChordModifiers.Alt;
        }

        if (keyData.HasFlag(Keys.Shift))
        {
            modifiers |= KeyChordModifiers.Shift;
        }

        canonical = new ParsedKeyChord(modifiers, KeyChord.Keys[keyName]).Canonical;
        return true;
    }

    /// <summary>Maps a captured <see cref="Keys"/> code onto its <see cref="KeyChord"/> table name; false = not in the curated set.</summary>
    private static bool TryMapCapturedKey(Keys keyCode, out string keyName)
    {
        keyName = keyCode switch
        {
            >= Keys.A and <= Keys.Z => keyCode.ToString(),
            >= Keys.D0 and <= Keys.D9 => keyCode.ToString()[1..], // "D7" -> "7"
            >= Keys.NumPad0 and <= Keys.NumPad9 =>
                ((char)('0' + (keyCode - Keys.NumPad0))).ToString(),
            >= Keys.F1 and <= Keys.F24 => keyCode.ToString(),
            Keys.Enter => "Enter",
            Keys.Tab => "Tab",
            Keys.Space => "Space",
            Keys.Up => "Up",
            Keys.Down => "Down",
            Keys.Left => "Left",
            Keys.Right => "Right",
            Keys.Home => "Home",
            Keys.End => "End",
            Keys.PageUp => "PageUp",
            Keys.PageDown => "PageDown",
            Keys.Insert => "Insert",
            Keys.Delete => "Delete",
            Keys.Back => "Backspace",
            Keys.PrintScreen => "PrintScreen",
            Keys.MediaPlayPause => "MediaPlayPause",
            Keys.MediaNextTrack => "MediaNext",
            Keys.MediaPreviousTrack => "MediaPrevious",
            Keys.MediaStop => "MediaStop",
            Keys.VolumeMute => "VolumeMute",
            Keys.VolumeUp => "VolumeUp",
            Keys.VolumeDown => "VolumeDown",
            _ => "",
        };
        return keyName.Length > 0;
    }
}
