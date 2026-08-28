using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace MatterHelm.Actions;

/// <summary>Modifier keys of a <see cref="ParsedKeyChord"/>, in canonical (hold) order: Ctrl, Alt, Shift, Win.</summary>
[Flags]
public enum KeyChordModifiers
{
    /// <summary>No modifier (a bare key, e.g. <c>F5</c>).</summary>
    None = 0,

    /// <summary>Ctrl (VK_CONTROL).</summary>
    Ctrl = 1,

    /// <summary>Alt (VK_MENU).</summary>
    Alt = 2,

    /// <summary>Shift (VK_SHIFT).</summary>
    Shift = 4,

    /// <summary>Win (VK_LWIN).</summary>
    Win = 8,
}

/// <summary>One entry of the <see cref="KeyChord"/> key table: canonical name, virtual-key code, and whether SendInput needs the extended-key flag.</summary>
public sealed record ChordKey(string Name, ushort VirtualKey, bool Extended);

/// <summary>A validated key sequence: modifiers plus one non-modifier key from the <see cref="KeyChord"/> table.</summary>
public sealed record ParsedKeyChord(KeyChordModifiers Modifiers, ChordKey Key)
{
    /// <summary>The canonical string form (<c>Ctrl+Shift+V</c>): modifiers in Ctrl/Alt/Shift/Win order, then the table-cased key name.</summary>
    public string Canonical =>
        (Modifiers.HasFlag(KeyChordModifiers.Ctrl) ? "Ctrl+" : "")
        + (Modifiers.HasFlag(KeyChordModifiers.Alt) ? "Alt+" : "")
        + (Modifiers.HasFlag(KeyChordModifiers.Shift) ? "Shift+" : "")
        + (Modifiers.HasFlag(KeyChordModifiers.Win) ? "Win+" : "")
        + Key.Name;
}

/// <summary>One synthesized keyboard event of a chord, as plain data (the pure seam the executor tests assert on): virtual-key and hardware scan codes, plus extended and release flags. Maps 1:1 onto a keyboard <c>INPUT</c> entry.</summary>
public sealed record KeyChordEvent(ushort VirtualKey, ushort ScanCode, bool Extended, bool KeyUp);

/// <summary>
/// The <c>keySequence</c> custom action (S7-1): parsing, validation, and
/// SendInput execution of a keyboard chord like <c>Ctrl+Shift+V</c>.
///
/// <para><b>Sequence grammar</b> (config wire format and Settings-dialog input):
/// <c>[Ctrl+][Alt+][Shift+][Win+]&lt;Key&gt;</c>. Matching is case-insensitive
/// and tolerates whitespace around <c>+</c>; modifiers may arrive in any order
/// but each at most once (a duplicate is invalid); the final segment must name
/// exactly one key from <see cref="Keys"/>. The canonical form — fixed
/// Ctrl/Alt/Shift/Win order, table casing — is what
/// <see cref="ParsedKeyChord.Canonical"/> returns and what Config persists.</para>
///
/// <para><b>Key table</b> (<see cref="Keys"/>, the single source shared by the
/// parser, the VK mapping, and the docs): <c>A</c>–<c>Z</c>, <c>0</c>–<c>9</c>,
/// <c>F1</c>–<c>F24</c>, and the curated named set — Enter, Tab, Esc, Space,
/// Up, Down, Left, Right, Home, End, PageUp, PageDown, Insert, Delete,
/// Backspace, PrintScreen, plus the media keys (MediaPlayPause, MediaNext,
/// MediaPrevious, MediaStop, VolumeMute, VolumeUp, VolumeDown) so a chordless
/// sequence can also reach what the <c>mediaKey</c> action covers. Arrow/nav
/// keys, Insert/Delete, PrintScreen, the media keys, and Win carry the
/// extended-key flag, matching their physical scan groups.</para>
///
/// <para><b>Synthesis</b>: modifiers down in canonical order, key down, key
/// up, modifiers up in reverse — one SendInput batch. Every event retains its
/// VK code and also carries the hardware scan code returned by MapVirtualKey;
/// KEYEVENTF_SCANCODE remains clear so Windows continues to resolve the VK
/// under the active layout while low-level hooks can match physical-key data.</para>
/// </summary>
public static partial class KeyChord
{
    private const uint MapVkVkToVsc = 0;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkShift = 0x10;
    private const ushort VkLWin = 0x5B;

    /// <summary>Canonical modifier order for both the string form and the key-down order (reversed for key-up).</summary>
    private static readonly (KeyChordModifiers Modifier, string Name, ushort VirtualKey, bool Extended)[] ModifierTable =
    [
        (KeyChordModifiers.Ctrl, "Ctrl", VkControl, false),
        (KeyChordModifiers.Alt, "Alt", VkMenu, false),
        (KeyChordModifiers.Shift, "Shift", VkShift, false),
        (KeyChordModifiers.Win, "Win", VkLWin, true), // VK_LWIN is an extended key
    ];

    /// <summary>The full key table, keyed case-insensitively by canonical name (see the class doc for the set).</summary>
    public static IReadOnlyDictionary<string, ChordKey> Keys { get; } = BuildKeyTable();

    /// <summary>
    /// Parses <paramref name="sequence"/> per the class-doc grammar.
    /// Returns true with the parsed chord, or false with a user-facing error
    /// (the Settings dialog shows it verbatim). Pure — no I/O, no throw.
    /// </summary>
    public static bool TryParse(
        string sequence,
        [NotNullWhen(true)] out ParsedKeyChord? chord,
        [NotNullWhen(false)] out string? error)
    {
        chord = null;
        string[] segments = sequence.Split('+');
        var modifiers = KeyChordModifiers.None;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            string segment = segments[i].Trim();
            (KeyChordModifiers Modifier, string Name, ushort VirtualKey, bool Extended) match =
                Array.Find(ModifierTable, m => m.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (match.Modifier == KeyChordModifiers.None)
            {
                error = segment.Length == 0
                    ? "Key sequence has an empty segment."
                    : $"\"{segment}\" is not a modifier (Ctrl, Alt, Shift, Win).";
                return false;
            }

            if (modifiers.HasFlag(match.Modifier))
            {
                error = $"Duplicate modifier \"{match.Name}\".";
                return false;
            }

            modifiers |= match.Modifier;
        }

        string keySegment = segments[^1].Trim();
        if (keySegment.Length == 0)
        {
            error = segments.Length == 1
                ? "Key sequence must not be empty."
                : "Key sequence has an empty segment.";
            return false;
        }

        if (!Keys.TryGetValue(keySegment, out ChordKey? key))
        {
            error = $"\"{keySegment}\" is not a known key. Use A-Z, 0-9, F1-F24, or a named key like Enter, Esc, or PageUp.";
            return false;
        }

        chord = new ParsedKeyChord(modifiers, key);
        error = null;
        return true;
    }

    /// <summary>
    /// The exact keyboard-event sequence a chord synthesizes (the pure seam
    /// tests assert on): modifiers down in canonical order, key down, key up,
    /// modifiers up in reverse.
    /// </summary>
    public static IReadOnlyList<KeyChordEvent> BuildEvents(ParsedKeyChord chord) =>
        BuildEvents(chord, MapScanCode);

    /// <summary>The deterministic event-construction seam; production supplies MapVirtualKey and tests supply a fixed mapper.</summary>
    internal static IReadOnlyList<KeyChordEvent> BuildEvents(
        ParsedKeyChord chord,
        Func<ushort, ushort> mapScanCode)
    {
        ArgumentNullException.ThrowIfNull(mapScanCode);
        List<KeyChordEvent> events = [];
        foreach ((KeyChordModifiers modifier, _, ushort virtualKey, bool extended) in ModifierTable)
        {
            if (chord.Modifiers.HasFlag(modifier))
            {
                events.Add(new KeyChordEvent(
                    virtualKey,
                    mapScanCode(virtualKey),
                    extended,
                    KeyUp: false));
            }
        }

        events.Add(new KeyChordEvent(
            chord.Key.VirtualKey,
            mapScanCode(chord.Key.VirtualKey),
            chord.Key.Extended,
            KeyUp: false));
        events.Add(events[^1] with { KeyUp = true });
        for (int i = events.Count - 3; i >= 0; i--)
        {
            KeyChordEvent down = events[i];
            events.Add(down with { KeyUp = true });
        }

        return events;
    }

    /// <summary>
    /// Injects the chord via one <c>SendInput</c> batch. Returns false (logged)
    /// if injection failed. SendInput cannot cross UIPI from an unelevated
    /// MatterHelm process into an elevated target and Windows may fail silently;
    /// users must run both processes at the same integrity level.
    /// </summary>
    public static bool Press(ParsedKeyChord chord)
    {
        IReadOnlyList<KeyChordEvent> events = BuildEvents(chord);
        var inputs = new NativeInput.Input[events.Count];
        for (int i = 0; i < inputs.Length; i++)
        {
            inputs[i].Type = NativeInput.InputKeyboard;
            inputs[i].Union.Keyboard.VirtualKey = events[i].VirtualKey;
            inputs[i].Union.Keyboard.ScanCode = events[i].ScanCode;
            inputs[i].Union.Keyboard.Flags =
                (events[i].Extended ? NativeInput.KeyEventFExtendedKey : 0)
                | (events[i].KeyUp ? NativeInput.KeyEventFKeyUp : 0);
        }

        return NativeInput.Send(inputs, $"key sequence '{chord.Canonical}'");
    }

    private static ushort MapScanCode(ushort virtualKey) =>
        (ushort)MapVirtualKey(virtualKey, MapVkVkToVsc);

    private static Dictionary<string, ChordKey> BuildKeyTable()
    {
        var table = new Dictionary<string, ChordKey>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, int virtualKey, bool extended = false) =>
            table.Add(name, new ChordKey(name, (ushort)virtualKey, extended));

        for (char c = 'A'; c <= 'Z'; c++)
        {
            Add(c.ToString(), c); // VK_A..VK_Z equal the ASCII uppercase letters
        }

        for (char c = '0'; c <= '9'; c++)
        {
            Add(c.ToString(), c); // VK_0..VK_9 equal the ASCII digits
        }

        for (int f = 1; f <= 24; f++)
        {
            Add($"F{f}", 0x70 + f - 1); // VK_F1 = 0x70
        }

        // The curated named set (class doc); extended = the physical
        // nav/arrow/media scan group, mirroring what a real keyboard sends.
        Add("Enter", 0x0D);
        Add("Tab", 0x09);
        Add("Esc", 0x1B);
        Add("Space", 0x20);
        Add("Up", 0x26, extended: true);
        Add("Down", 0x28, extended: true);
        Add("Left", 0x25, extended: true);
        Add("Right", 0x27, extended: true);
        Add("Home", 0x24, extended: true);
        Add("End", 0x23, extended: true);
        Add("PageUp", 0x21, extended: true);
        Add("PageDown", 0x22, extended: true);
        Add("Insert", 0x2D, extended: true);
        Add("Delete", 0x2E, extended: true);
        Add("Backspace", 0x08);
        Add("PrintScreen", 0x2C, extended: true);
        Add("MediaPlayPause", 0xB3, extended: true);
        Add("MediaNext", 0xB0, extended: true);
        Add("MediaPrevious", 0xB1, extended: true);
        Add("MediaStop", 0xB2, extended: true);
        Add("VolumeMute", 0xAD, extended: true);
        Add("VolumeUp", 0xAF, extended: true);
        Add("VolumeDown", 0xAE, extended: true);
        return table;
    }

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static partial uint MapVirtualKey(uint code, uint mapType);
}
