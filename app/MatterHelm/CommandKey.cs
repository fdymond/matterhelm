using System.Text.RegularExpressions;

namespace MatterHelm;

/// <summary>
/// The one command-key (slug) rule, shared by the config schema and the wire
/// protocol (ADR-004 §1/§3): a custom command's <c>key</c> is its Matter
/// endpoint id and wire identifier, so <see cref="Config"/> validation and
/// <c>Sidecar/Protocol.cs</c> must accept exactly the same strings as the
/// bridge's zod schema — kebab-case (<c>^[a-z0-9]+(-[a-z0-9]+)*$</c>), at most
/// <see cref="MaxLength"/> characters.
/// </summary>
public static partial class CommandKey
{
    /// <summary>Maximum key length in characters (ADR-004 §3).</summary>
    public const int MaxLength = 64;

    /// <summary>True iff <paramref name="key"/> is a valid kebab-case command key.</summary>
    public static bool IsValid(string key) => key.Length is > 0 and <= MaxLength && SlugRegex().IsMatch(key);

    // \z, not $: .NET's $ also matches before a trailing newline, which
    // JavaScript's (zod's) $ does not — "movie-mode\n" must reject on both
    // sides of the wire.
    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*\z")]
    private static partial Regex SlugRegex();
}
