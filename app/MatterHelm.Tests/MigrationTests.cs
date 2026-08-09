using Xunit;

namespace MatterHelm.Tests;

/// <summary>
/// Tests for the S7-2 %APPDATA% root migration (HtpcMatterBridge → MatterHelm):
/// the pure decision rule, and <see cref="Migration.Run"/> against throwaway
/// temp directories — fresh install, old-only (moves, fabric storage present
/// after), both-exist (new wins + WARN, old untouched), and move-failure via a
/// locked file (falls back to the old root, nothing half-copied). Never the
/// real user profile (CLAUDE.md ground rule 4).
/// </summary>
public sealed class MigrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _oldRoot;
    private readonly string _newRoot;
    private readonly TestSupport.LogCapture _log = new();

    public MigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _oldRoot = Path.Combine(_dir, "HtpcMatterBridge");
        _newRoot = Path.Combine(_dir, "MatterHelm");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leftover temp dir is not worth failing the test run over.
        }
    }

    /// <summary>Seeds a legacy root shaped like a real install: config.json, a log, and the matter\ fabric storage.</summary>
    private void SeedOldRoot()
    {
        Directory.CreateDirectory(Path.Combine(_oldRoot, "logs"));
        Directory.CreateDirectory(Path.Combine(_oldRoot, "matter", "htpc-bridge"));
        File.WriteAllText(Path.Combine(_oldRoot, "config.json"), """{"ipcPort":39531}""");
        File.WriteAllText(Path.Combine(_oldRoot, "logs", "app-20260801.log"), "old log line");
        File.WriteAllText(Path.Combine(_oldRoot, "matter", "htpc-bridge", "fabrics.json"), "fabric-credentials");
    }

    [Theory]
    [InlineData(false, false, MigrationAction.None)]
    [InlineData(false, true, MigrationAction.None)]
    [InlineData(true, false, MigrationAction.Move)]
    [InlineData(true, true, MigrationAction.PreferNew)]
    public void DecideCoversAllExistenceCombinations(bool oldExists, bool newExists, MigrationAction expected)
    {
        Assert.Equal(expected, Migration.Decide(oldExists, newExists));
    }

    [Fact]
    public void FreshInstallUsesNewRootAndTouchesNothing()
    {
        MigrationResult result = Migration.Run(_oldRoot, _newRoot, _log.Sink);

        Assert.Equal(MigrationOutcome.NoLegacyData, result.Outcome);
        Assert.Equal(_newRoot, result.EffectiveRoot);
        Assert.False(Directory.Exists(_oldRoot));
        Assert.False(Directory.Exists(_newRoot));
        Assert.Empty(_log.Snapshot());
    }

    [Fact]
    public void AlreadyMigratedInstallUsesNewRootSilently()
    {
        Directory.CreateDirectory(_newRoot);
        File.WriteAllText(Path.Combine(_newRoot, "config.json"), "{}");

        MigrationResult result = Migration.Run(_oldRoot, _newRoot, _log.Sink);

        Assert.Equal(MigrationOutcome.NoLegacyData, result.Outcome);
        Assert.Equal(_newRoot, result.EffectiveRoot);
        Assert.Empty(_log.Snapshot());
    }

    [Fact]
    public void OldOnlyMovesEverythingIncludingFabricStorage()
    {
        SeedOldRoot();

        MigrationResult result = Migration.Run(_oldRoot, _newRoot, _log.Sink);

        Assert.Equal(MigrationOutcome.Migrated, result.Outcome);
        Assert.Equal(_newRoot, result.EffectiveRoot);
        Assert.False(Directory.Exists(_oldRoot));
        Assert.Equal("""{"ipcPort":39531}""", File.ReadAllText(Path.Combine(_newRoot, "config.json")));
        Assert.Equal("old log line", File.ReadAllText(Path.Combine(_newRoot, "logs", "app-20260801.log")));

        // The re-pairing-critical piece: the matter\ fabric storage (node dir
        // name unchanged) arrived byte-for-byte.
        Assert.Equal(
            "fabric-credentials",
            File.ReadAllText(Path.Combine(_newRoot, "matter", "htpc-bridge", "fabrics.json")));
    }

    [Fact]
    public void OldOnlyLogsTheLoudUnambiguousMigrationLine()
    {
        SeedOldRoot();

        Migration.Run(_oldRoot, _newRoot, _log.Sink);

        Assert.True(_log.Contains("INFO", $"migrated {_oldRoot} -> {_newRoot}"));
    }

    [Fact]
    public void BothExistPrefersNewWarnsAndLeavesOldUntouched()
    {
        SeedOldRoot();
        Directory.CreateDirectory(_newRoot);
        File.WriteAllText(Path.Combine(_newRoot, "config.json"), """{"ipcPort":40000}""");

        MigrationResult result = Migration.Run(_oldRoot, _newRoot, _log.Sink);

        Assert.Equal(MigrationOutcome.BothExist, result.Outcome);
        Assert.Equal(_newRoot, result.EffectiveRoot);
        Assert.True(_log.Contains("WARN", "orphaned"));
        Assert.True(_log.ContainsMessage(_oldRoot));
        Assert.True(_log.ContainsMessage(_newRoot));

        // Neither side modified: new keeps its own config, old keeps everything.
        Assert.Equal("""{"ipcPort":40000}""", File.ReadAllText(Path.Combine(_newRoot, "config.json")));
        Assert.Equal(
            "fabric-credentials",
            File.ReadAllText(Path.Combine(_oldRoot, "matter", "htpc-bridge", "fabrics.json")));
    }

    [Fact]
    public void DefaultPathsDeriveFromTheEffectiveRoot()
    {
        // The whole point of AppPaths: every default path follows Root, so a
        // MoveFailed fallback (Root = legacy dir) repoints config, fabric
        // storage, and logs together. Read-only assertions — mutating the
        // global Root here would race parallel test classes.
        Assert.Equal(Path.Combine(AppPaths.Root, "config.json"), Config.DefaultPath);
        Assert.Equal(Path.Combine(AppPaths.Root, "matter"), Ui.SettingsViewModel.StorageDirDisplay);
        Assert.Equal("MatterHelm", Path.GetFileName(AppPaths.DefaultRoot));
        Assert.Equal("HtpcMatterBridge", Path.GetFileName(AppPaths.LegacyRoot));
    }

    [Fact]
    public void MoveFailureFallsBackToOldRootWithoutLosingData()
    {
        SeedOldRoot();

        // Hold the fabric file open with no sharing: Directory.Move of the
        // tree now fails — the "old root locked by another process" case.
        using (new FileStream(
            Path.Combine(_oldRoot, "matter", "htpc-bridge", "fabrics.json"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None))
        {
            MigrationResult result = Migration.Run(_oldRoot, _newRoot, _log.Sink);

            Assert.Equal(MigrationOutcome.MoveFailed, result.Outcome);
            Assert.Equal(_oldRoot, result.EffectiveRoot);
            Assert.True(_log.Contains("WARN", "could not migrate"));
        }

        // Never lose data, never half-copy: the old tree is intact and no
        // partial new tree was created (same-volume rename is atomic).
        Assert.Equal(
            "fabric-credentials",
            File.ReadAllText(Path.Combine(_oldRoot, "matter", "htpc-bridge", "fabrics.json")));
        Assert.Equal("""{"ipcPort":39531}""", File.ReadAllText(Path.Combine(_oldRoot, "config.json")));
        Assert.False(Directory.Exists(_newRoot));
    }
}
