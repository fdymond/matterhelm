using MatterHelm.Infrastructure;
using Xunit;

namespace MatterHelm.Tests;

public sealed class MatterStorageResetterTests
{
    [Fact]
    public void FailedAtomicRenameLeavesTheLiveDirectoryUntouched()
    {
        var operations = new FakeDirectoryOperations { MoveFailure = new IOException("locked") };
        var time = new AdvancingTimeProvider();
        var resetter = new MatterStorageResetter(
            (_, _) => { },
            operations,
            time,
            time.Advance);

        MatterStorageResetResult result = resetter.Reset("C:\\MatterHelm\\matter");

        Assert.False(result.Completed);
        Assert.Equal("locked", result.Error);
        Assert.True(operations.LiveExists);
        Assert.Equal(0, operations.DeleteCalls);
    }

    [Fact]
    public void FailedStagedDeleteCompletesResetAndLogsTheResiduePath()
    {
        var operations = new FakeDirectoryOperations { DeleteFailure = new IOException("scanner lock") };
        var time = new AdvancingTimeProvider();
        var logs = new List<(string Level, string Message)>();
        var resetter = new MatterStorageResetter(
            (level, message) => logs.Add((level, message)),
            operations,
            time,
            time.Advance);

        MatterStorageResetResult result = resetter.Reset("C:\\MatterHelm\\matter");

        Assert.True(result.Completed);
        Assert.Null(result.Error);
        Assert.False(operations.LiveExists);
        Assert.NotNull(result.ResiduePath);
        Assert.True(operations.StagedExists);
        Assert.Contains(logs, entry =>
            entry.Level == "WARN"
            && entry.Message.Contains("residue left at", StringComparison.Ordinal)
            && entry.Message.Contains(result.ResiduePath, StringComparison.Ordinal));
    }

    [Fact]
    public void SuccessfulResetSweepsStaleSiblingResidueBestEffort()
    {
        const string Stale = "C:\\MatterHelm\\matter.reset-old";
        var operations = new FakeDirectoryOperations { StaleDirectories = [Stale] };
        var time = new AdvancingTimeProvider();
        var logs = new List<(string Level, string Message)>();
        var resetter = new MatterStorageResetter(
            (level, message) => logs.Add((level, message)),
            operations,
            time,
            time.Advance);

        MatterStorageResetResult result = resetter.Reset("C:\\MatterHelm\\matter");

        Assert.True(result.Completed);
        Assert.Contains(Stale, operations.DeletedPaths);
        Assert.Contains(logs, entry =>
            entry.Level == "DEBUG" && entry.Message.Contains("stale Matter reset residue", StringComparison.Ordinal));
    }

    private sealed class FakeDirectoryOperations : IStorageDirectoryOperations
    {
        public Exception? MoveFailure { get; init; }

        public Exception? DeleteFailure { get; init; }

        public bool LiveExists { get; private set; } = true;

        public bool StagedExists { get; private set; }

        public int DeleteCalls { get; private set; }

        public IReadOnlyList<string> StaleDirectories { get; init; } = [];

        public List<string> DeletedPaths { get; } = [];

        public bool Exists(string path) => LiveExists;

        public void Move(string source, string destination)
        {
            if (MoveFailure is not null)
            {
                throw MoveFailure;
            }

            LiveExists = false;
            StagedExists = true;
        }

        public void Delete(string path, bool recursive)
        {
            DeleteCalls++;
            DeletedPaths.Add(path);
            if (DeleteFailure is not null)
            {
                throw DeleteFailure;
            }

            StagedExists = false;
        }

        public IEnumerable<string> EnumerateDirectories(string path, string searchPattern) => StaleDirectories;
    }

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
