namespace MatterHelm;

/// <summary>
/// Decides the seed every Matter endpoint's stable identity
/// (serialNumber/uniqueId) derives from (S10-4).
///
/// <para><b>Why this exists.</b> Before S10-4 the seed was a constant compiled
/// into the bridge, so every MatterHelm install on earth derived byte-identical
/// endpoint <c>UniqueID</c>s. Harmless across separate homes (fabrics never
/// meet), but two PCs in ONE home advertised colliding identities — which the
/// Matter spec forbids and Google Home has no way to tell apart.</para>
///
/// <para><b>Why it is not simply "always random".</b> Changing the seed of an
/// already-paired install changes every endpoint's identity, which Google Home
/// reads as a different set of devices — silently unpairing the user. So the
/// decision is made once, on the first run that has no seed persisted:
/// an install that already has Matter fabric storage keeps
/// <see cref="LegacySeed"/> (its identity is already known to Google), and a
/// genuinely fresh install mints a random one. Either way the answer is
/// written to config, so it never has to be inferred again.</para>
/// </summary>
public static class MatterIdentity
{
    /// <summary>
    /// The pre-S10-4 compiled-in seed. Existing installs pin to this so their
    /// paired devices keep the identity Google already has — never change it.
    /// </summary>
    public const string LegacySeed = "htpc-matter-bridge";

    /// <summary>
    /// Resolves the seed to persist and hand the sidecar.
    /// </summary>
    /// <param name="configuredSeed">The seed already in config, if any (a value here always wins — the user's or an earlier run's decision).</param>
    /// <param name="hasExistingFabric">True when Matter storage already exists, i.e. this install predates S10-4 (or has been paired) and must not have its identity changed.</param>
    /// <param name="mintSeed">Fresh-seed factory; defaults to <see cref="MintSeed"/>. Injectable so tests stay deterministic.</param>
    public static string Resolve(string? configuredSeed, bool hasExistingFabric, Func<string>? mintSeed = null) =>
        string.IsNullOrWhiteSpace(configuredSeed)
            ? hasExistingFabric ? LegacySeed : (mintSeed ?? MintSeed)()
            : configuredSeed.Trim();

    /// <summary>A fresh per-install seed: 32 hex chars, no dashes (the value is a hash input, never shown to Google).</summary>
    public static string MintSeed() => Guid.NewGuid().ToString("N");
}
