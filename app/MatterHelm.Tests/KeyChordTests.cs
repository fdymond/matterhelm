using MatterHelm.Actions;
using Xunit;

namespace MatterHelm.Tests;

/// <summary>
/// Behaviour tests for <see cref="KeyChord"/> (S7-1): the sequence grammar
/// (case-insensitive in, canonical out, duplicate modifier / unknown key
/// invalid), the one key table shared by parser and VK mapping, and the exact
/// keyboard-event sequence a chord synthesizes (the pure seam under
/// <c>SendInput</c> — no test ever injects real input).
/// </summary>
public static class KeyChordTests
{
    private static ushort StubScanCode(ushort virtualKey) => (ushort)(virtualKey + 1);

    private static ParsedKeyChord Parse(string sequence)
    {
        Assert.True(KeyChord.TryParse(sequence, out ParsedKeyChord? chord, out string? error), error);
        return chord!;
    }

    public sealed class Grammar
    {
        [Theory]
        [InlineData("Ctrl+Shift+V", "Ctrl+Shift+V")]
        [InlineData("ctrl+shift+v", "Ctrl+Shift+V")] // case-insensitive input, canonical casing out
        [InlineData("SHIFT+CTRL+V", "Ctrl+Shift+V")] // any modifier order in, fixed order out
        [InlineData("ctrl+alt+shift+win+delete", "Ctrl+Alt+Shift+Win+Delete")]
        [InlineData("win+e", "Win+E")]
        [InlineData("f5", "F5")] // bare key, no modifier
        [InlineData("alt+f4", "Alt+F4")]
        [InlineData(" Ctrl + Shift + V ", "Ctrl+Shift+V")] // whitespace around segments tolerated
        [InlineData("shift+pageup", "Shift+PageUp")]
        [InlineData("esc", "Esc")]
        [InlineData("7", "7")]
        public void ValidSequencesParseToTheCanonicalForm(string input, string canonical)
        {
            Assert.Equal(canonical, Parse(input).Canonical);
        }

        [Fact]
        public void ModifierFlagsMatchTheParsedSegments()
        {
            ParsedKeyChord chord = Parse("ctrl+shift+v");
            Assert.Equal(KeyChordModifiers.Ctrl | KeyChordModifiers.Shift, chord.Modifiers);
            Assert.Equal("V", chord.Key.Name);
        }

        [Theory]
        [InlineData("Ctrl+Ctrl+V")] // duplicate modifier
        [InlineData("ctrl+CTRL+v")] // duplicate modifier, mixed case
        [InlineData("Ctrl+Shift+Bogus")] // unknown key
        [InlineData("Foo+V")] // unknown modifier
        [InlineData("Ctrl")] // modifier alone is not a key
        [InlineData("Ctrl+")] // trailing separator
        [InlineData("Ctrl++V")] // empty segment
        [InlineData("+V")] // leading separator
        [InlineData("")] // empty
        [InlineData("   ")] // whitespace only
        [InlineData("Ctrl-Shift-V")] // wrong separator reads as one unknown key
        public void InvalidSequencesAreRejectedWithAnError(string input)
        {
            Assert.False(KeyChord.TryParse(input, out _, out string? error));
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        [Fact]
        public void DuplicateModifierNamesTheModifierInTheError()
        {
            Assert.False(KeyChord.TryParse("shift+shift+a", out _, out string? error));
            Assert.Contains("Duplicate modifier \"Shift\"", error, StringComparison.Ordinal);
        }

        [Fact]
        public void UnknownKeyNamesTheOffendingSegmentInTheError()
        {
            Assert.False(KeyChord.TryParse("Ctrl+Hyper", out _, out string? error));
            Assert.Contains("\"Hyper\"", error, StringComparison.Ordinal);
        }
    }

    public sealed class KeyTable
    {
        [Fact]
        public void CoversLettersDigitsFunctionKeysAndTheCuratedNamedSet()
        {
            // 26 letters + 10 digits + 24 F-keys + 16 named + 7 media keys.
            Assert.Equal(83, KeyChord.Keys.Count);
        }

        [Fact]
        public void EveryTableEntryParsesAsABareSequenceUnderItsOwnName()
        {
            foreach ((string name, ChordKey key) in KeyChord.Keys)
            {
                Assert.Equal(key, Parse(name).Key);
                Assert.Equal(name, key.Name);
            }
        }

        [Theory]
        [InlineData("A", 0x41, false)]
        [InlineData("Z", 0x5A, false)]
        [InlineData("0", 0x30, false)]
        [InlineData("9", 0x39, false)]
        [InlineData("F1", 0x70, false)]
        [InlineData("F24", 0x87, false)]
        [InlineData("Enter", 0x0D, false)]
        [InlineData("Tab", 0x09, false)]
        [InlineData("Esc", 0x1B, false)]
        [InlineData("Space", 0x20, false)]
        [InlineData("Backspace", 0x08, false)]
        [InlineData("Up", 0x26, true)]
        [InlineData("Down", 0x28, true)]
        [InlineData("Left", 0x25, true)]
        [InlineData("Right", 0x27, true)]
        [InlineData("Home", 0x24, true)]
        [InlineData("End", 0x23, true)]
        [InlineData("PageUp", 0x21, true)]
        [InlineData("PageDown", 0x22, true)]
        [InlineData("Insert", 0x2D, true)]
        [InlineData("Delete", 0x2E, true)]
        [InlineData("PrintScreen", 0x2C, true)]
        [InlineData("MediaPlayPause", 0xB3, true)]
        [InlineData("MediaNext", 0xB0, true)]
        [InlineData("MediaPrevious", 0xB1, true)]
        [InlineData("MediaStop", 0xB2, true)]
        [InlineData("VolumeMute", 0xAD, true)]
        [InlineData("VolumeUp", 0xAF, true)]
        [InlineData("VolumeDown", 0xAE, true)]
        public void MapsNamesToTheDocumentedVirtualKeysAndExtendedFlags(string name, int virtualKey, bool extended)
        {
            ChordKey key = KeyChord.Keys[name];
            Assert.Equal((ushort)virtualKey, key.VirtualKey);
            Assert.Equal(extended, key.Extended);
        }
    }

    public sealed class EventSynthesis
    {
        [Fact]
        public void CtrlShiftVBuildsTheExactSixEventInputSequence()
        {
            // The story's evidence case: modifiers down in order, key
            // down/up, modifiers up in reverse — VK codes per the one table.
            IReadOnlyList<KeyChordEvent> events = KeyChord.BuildEvents(Parse("Ctrl+Shift+V"), StubScanCode);

            Assert.Equal(
                [
                    new KeyChordEvent(0x11, 0x12, Extended: false, KeyUp: false), // Ctrl down
                    new KeyChordEvent(0x10, 0x11, Extended: false, KeyUp: false), // Shift down
                    new KeyChordEvent(0x56, 0x57, Extended: false, KeyUp: false), // V down
                    new KeyChordEvent(0x56, 0x57, Extended: false, KeyUp: true),  // V up
                    new KeyChordEvent(0x10, 0x11, Extended: false, KeyUp: true),  // Shift up
                    new KeyChordEvent(0x11, 0x12, Extended: false, KeyUp: true),  // Ctrl up
                ],
                events);
        }

        [Fact]
        public void AllFourModifiersPressInCanonicalOrderAndReleaseInReverse()
        {
            IReadOnlyList<KeyChordEvent> events = KeyChord.BuildEvents(Parse("Ctrl+Alt+Shift+Win+A"), StubScanCode);

            Assert.Equal(
                [0x11, 0x12, 0x10, 0x5B, 0x41, 0x41, 0x5B, 0x10, 0x12, 0x11],
                events.Select(e => (int)e.VirtualKey));
            Assert.Equal(
                [false, false, false, false, false, true, true, true, true, true],
                events.Select(e => e.KeyUp));
        }

        [Fact]
        public void ExtendedKeysCarryTheExtendedFlagOnBothDownAndUp()
        {
            IReadOnlyList<KeyChordEvent> events = KeyChord.BuildEvents(Parse("Win+Left"), StubScanCode);

            Assert.Equal(
                [
                    new KeyChordEvent(0x5B, 0x5C, Extended: true, KeyUp: false), // Win down (VK_LWIN is extended)
                    new KeyChordEvent(0x25, 0x26, Extended: true, KeyUp: false), // Left down
                    new KeyChordEvent(0x25, 0x26, Extended: true, KeyUp: true),  // Left up
                    new KeyChordEvent(0x5B, 0x5C, Extended: true, KeyUp: true),  // Win up
                ],
                events);
        }

        [Fact]
        public void ABareKeyBuildsJustItsDownUpPair()
        {
            IReadOnlyList<KeyChordEvent> events = KeyChord.BuildEvents(Parse("F5"), StubScanCode);

            Assert.Equal(
                [
                    new KeyChordEvent(0x74, 0x75, Extended: false, KeyUp: false),
                    new KeyChordEvent(0x74, 0x75, Extended: false, KeyUp: true),
                ],
                events);
        }

        [Fact]
        public void ScanCodeMapperRunsOncePerPressedKeyAndFeedsBothTransitions()
        {
            List<ushort> mappedVirtualKeys = [];
            ushort Map(ushort virtualKey)
            {
                mappedVirtualKeys.Add(virtualKey);
                return virtualKey switch
                {
                    0x11 => 0x1D,
                    0x10 => 0x2A,
                    0x7C => 0x64,
                    _ => throw new InvalidOperationException(),
                };
            }

            IReadOnlyList<KeyChordEvent> events = KeyChord.BuildEvents(Parse("Ctrl+Shift+F13"), Map);

            Assert.Equal([0x11, 0x10, 0x7C], mappedVirtualKeys.Select(key => (int)key));
            Assert.Equal([0x1D, 0x2A, 0x64, 0x64, 0x2A, 0x1D], events.Select(entry => (int)entry.ScanCode));
        }

        [Fact]
        public void WindowsMapVirtualKeyPopulatesTheNeutralChordScanCodes()
        {
            IReadOnlyList<KeyChordEvent> events = KeyChord.BuildEvents(Parse("Ctrl+Shift+F13"));

            Assert.Equal([0x1D, 0x2A, 0x64, 0x64, 0x2A, 0x1D], events.Select(entry => (int)entry.ScanCode));
            Assert.DoesNotContain(events, entry => entry.ScanCode == 0);
        }

        [Fact]
        public void WindowsMapVirtualKeyPopulatesEverySupportedKey()
        {
            foreach (ChordKey key in KeyChord.Keys.Values)
            {
                var chord = new ParsedKeyChord(KeyChordModifiers.None, key);
                Assert.DoesNotContain(KeyChord.BuildEvents(chord), entry => entry.ScanCode == 0);
            }
        }
    }
}
