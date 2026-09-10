using MatterHelm.Actions;

namespace MatterHelm.Ui;

/// <summary>Returns the canonical form of a key sequence that the validator has already accepted.</summary>
internal static class KeySequenceCanonicalizer
{
    internal static string Canonicalize(string sequence) =>
        KeyChord.TryParse(sequence, out ParsedKeyChord? chord, out _) ? chord.Canonical : sequence;
}
