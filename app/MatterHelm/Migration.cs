namespace MatterHelm;

/// <summary>What <see cref="Migration.Decide"/> concluded should happen.</summary>
public enum MigrationAction
{
    /// <summary>No legacy directory — nothing to migrate.</summary>
    None,

    /// <summary>Legacy directory only — move it to the new root.</summary>
    Move,

    /// <summary>Both roots exist — use the new one, warn about the orphan.</summary>
    PreferNew,
}

/// <summary>Which path the migration actually took.</summary>
public enum MigrationOutcome
{
    /// <summary>Fresh install or already migrated — the new root is in effect.</summary>
    NoLegacyData,

    /// <summary>The legacy root was moved to the new root (atomic rename; fabric storage carried over).</summary>
    Migrated,

    /// <summary>Both roots existed; the new root wins and the legacy one is orphaned (WARN).</summary>
    BothExist,

    /// <summary>The move failed (legacy root locked); the LEGACY root stays in effect this session (WARN). Nothing was copied or lost.</summary>
    MoveFailed,
}

/// <summary>The migration's verdict: the data root the app must use this session, and why.</summary>
public sealed record MigrationResult(string EffectiveRoot, MigrationOutcome Outcome);

/// <summary>
/// One-time %APPDATA% root migration for the S7-2 product rename
/// (<c>HtpcMatterBridge</c> → <c>MatterHelm</c>). Runs at startup BEFORE
/// Config/Log touch disk (creating the new root first would make the atomic
/// <see cref="Directory.Move(string, string)"/> impossible). The move carries
/// config.json, logs, metrics snapshots and — crucially — the <c>matter\</c>
/// fabric storage, so the existing Google Home pairing survives the rename.
/// Failure modes never lose data: a locked legacy directory falls back to
/// using the legacy root for this session (retry next start); when both roots
/// exist the new one wins and the legacy one is left untouched.
/// </summary>
public static class Migration
{
    /// <summary>Pure decision rule — trivially unit-testable without IO.</summary>
    public static MigrationAction Decide(bool oldExists, bool newExists) =>
        !oldExists ? MigrationAction.None
        : newExists ? MigrationAction.PreferNew
        : MigrationAction.Move;

    /// <summary>
    /// Applies <see cref="Decide"/> to the real directories. Same-volume
    /// <see cref="Directory.Move(string, string)"/> is an atomic rename — the
    /// data is either fully at <paramref name="newRoot"/> or untouched at
    /// <paramref name="oldRoot"/>, never half-copied.
    /// </summary>
    /// <param name="oldRoot">The legacy data root (<see cref="AppPaths.LegacyRoot"/> in production; a temp dir in tests).</param>
    /// <param name="newRoot">The new data root (<see cref="AppPaths.DefaultRoot"/> in production; a temp dir in tests).</param>
    /// <param name="log">Log sink (level, message). <c>Program</c> buffers these until the file log — whose directory depends on this result — is up.</param>
    public static MigrationResult Run(string oldRoot, string newRoot, Action<string, string> log)
    {
        switch (Decide(Directory.Exists(oldRoot), Directory.Exists(newRoot)))
        {
            case MigrationAction.None:
                return new MigrationResult(newRoot, MigrationOutcome.NoLegacyData);

            case MigrationAction.PreferNew:
                log("WARN",
                    $"Both {oldRoot} and {newRoot} exist; using {newRoot}. "
                    + $"The old directory is orphaned and no longer read — delete {oldRoot} manually if nothing in it is needed.");
                return new MigrationResult(newRoot, MigrationOutcome.BothExist);

            default:
                try
                {
                    Directory.Move(oldRoot, newRoot);
                    log("INFO",
                        $"migrated {oldRoot} -> {newRoot} "
                        + "(config.json, logs, metrics, and Matter fabric storage moved; the Google Home pairing is preserved).");
                    return new MigrationResult(newRoot, MigrationOutcome.Migrated);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log("WARN",
                        $"could not migrate {oldRoot} -> {newRoot} ({ex.Message}); "
                        + "using the old directory this session — no data was moved or lost; migration is retried on next start.");
                    return new MigrationResult(oldRoot, MigrationOutcome.MoveFailed);
                }
        }
    }
}
