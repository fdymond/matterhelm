using Xunit;

namespace MatterHelm.Tests;

[Collection(DiagnosticsTests.StaticLogState.Name)]
public sealed class LogResourceTests : IDisposable
{
    private readonly string _directory;
    private readonly string _originalDirectory;
    private readonly LogLevel _originalMinimum;
    private readonly TimeProvider _originalClock;
    private readonly long _originalFileSizeLimitBytes;
    private readonly int _originalSegmentsPerDayLimit;

    public LogResourceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _originalDirectory = Log.LogDirectory;
        _originalMinimum = Log.MinimumLevel;
        _originalClock = Log.Clock;
        _originalFileSizeLimitBytes = Log.FileSizeLimitBytes;
        _originalSegmentsPerDayLimit = Log.SegmentsPerDayLimit;
        Log.LogDirectory = _directory;
        Log.MinimumLevel = LogLevel.Debug;
        Log.Clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        Log.LogDirectory = _originalDirectory;
        Log.MinimumLevel = _originalMinimum;
        Log.Clock = _originalClock;
        Log.FileSizeLimitBytes = _originalFileSizeLimitBytes;
        Log.SegmentsPerDayLimit = _originalSegmentsPerDayLimit;
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void RepeatedWritesReuseTheCachedActiveSegment()
    {
        Log.FileSizeLimitBytes = 1024 * 1024;

        for (int i = 0; i < 10; i++)
        {
            Log.Info($"cached write {i}");
        }

        Assert.Equal(1, Log.SegmentScanCount);
        Assert.Single(Directory.EnumerateFiles(_directory, "app-*.log"));
    }

    [Fact]
    public void FullDayEvictsTheOldestSegmentAndWritesOneSummary()
    {
        Log.FileSizeLimitBytes = 1;
        Log.SegmentsPerDayLimit = 3;

        for (int i = 0; i < 4; i++)
        {
            Log.Info($"segment {i}");
        }

        string[] paths = [.. Directory.EnumerateFiles(_directory, "app-*.log")];
        Assert.Equal(3, paths.Length);
        string[] lines = [.. paths.SelectMany(File.ReadAllLines)];
        Assert.Single(lines, line => line.Contains("log segment cap reached; evicted 1 oldest segment", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.EndsWith("segment 0", StringComparison.Ordinal));
    }

    [Fact]
    public void LockedOldestSegmentCachesOneFailedDeleteAttemptWithoutDroppingLines()
    {
        Log.FileSizeLimitBytes = 1;
        Log.SegmentsPerDayLimit = 3;
        Log.Info("segment 0");
        Log.Info("segment 1");
        Log.Info("segment 2");
        string oldest = Path.Combine(_directory, "app-20260910.log");
        using var held = new FileStream(oldest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Log.Error("must survive failed eviction");
        Log.Error("second write must not retry eviction yet");

        string[] lines = [.. Directory.EnumerateFiles(_directory, "app-*.log").SelectMany(File.ReadAllLines)];
        Assert.Contains(lines, line => line.EndsWith("[ERROR] must survive failed eviction", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.EndsWith("[ERROR] second write must not retry eviction yet", StringComparison.Ordinal));
        Assert.Equal(3, Directory.EnumerateFiles(_directory, "app-*.log").Count());
        Assert.Equal(1, Log.SegmentDeleteAttemptCount);
    }

    [Fact]
    public void NewSegmentIndexesKeepIncreasingAfterEviction()
    {
        Log.FileSizeLimitBytes = 1;
        Log.SegmentsPerDayLimit = 3;

        for (int i = 0; i < 4; i++)
        {
            Log.Info($"segment {i}");
        }

        Assert.Equal(
            ["app-20260910-001.log", "app-20260910-002.log", "app-20260910-003.log"],
            Directory.EnumerateFiles(_directory, "app-*.log").Select(Path.GetFileName).Order());
    }

    [Fact]
    public void SameLengthCaseVariantSegmentNameIsSkippedWithoutDroppingTheWrite()
    {
        Log.FileSizeLimitBytes = 1;
        File.WriteAllText(Path.Combine(_directory, "APP-20260910.log"), "occupied");

        Log.Warn("case variant must not drop this line");

        string content = string.Join('\n', Directory.EnumerateFiles(_directory, "app-*.log").SelectMany(File.ReadAllLines));
        Assert.Contains("[WARN] case variant must not drop this line", content, StringComparison.Ordinal);
    }

    [Fact]
    public void IdenticalWarningBurstKeepsTwentyLinesAndOneSuppressionSummary()
    {
        for (int i = 0; i < 25; i++)
        {
            Log.Warn("repeat marker");
        }

        Log.Info("flush repeat summary");

        string[] lines =
        [
            .. Directory.EnumerateFiles(_directory, "app-*.log")
                .SelectMany(File.ReadAllLines),
        ];
        Assert.Equal(20, lines.Count(line => line.EndsWith("[WARN] repeat marker", StringComparison.Ordinal)));
        Assert.Single(lines, line => line.Contains("suppressed 5 repeats of: repeat marker", StringComparison.Ordinal));
    }

    [Fact]
    public void FlushWritesAPendingSuppressionSummaryWithoutANextLine()
    {
        for (int i = 0; i < 25; i++)
        {
            Log.Error("shutdown repeat");
        }

        Log.Flush();

        string content = string.Join('\n', Directory.EnumerateFiles(_directory, "app-*.log").SelectMany(File.ReadAllLines));
        Assert.Contains("suppressed 5 repeats of: shutdown repeat", content, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownInjectedLogLevelDefaultsToWarn()
    {
        Log.Write("unexpected", "unknown level marker");

        string content = string.Join('\n', Directory.EnumerateFiles(_directory, "app-*.log").SelectMany(File.ReadAllLines));
        Assert.Contains("[WARN] unknown level marker", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgramFlushesPendingLogSummaryWhenItsBodyThrows()
    {
        for (int i = 0; i < 25; i++)
        {
            Log.Error("finally flush marker");
        }

        Assert.Throws<InvalidOperationException>(() => Program.RunWithLogFlush(
            () => throw new InvalidOperationException("exit path")));

        string content = string.Join('\n', Directory.EnumerateFiles(_directory, "app-*.log").SelectMany(File.ReadAllLines));
        Assert.Contains("suppressed 5 repeats of: finally flush marker", content, StringComparison.Ordinal);
    }

    [Fact]
    public void DayRolloverWritesAPendingSuppressionSummary()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 10, 23, 59, 0, TimeSpan.Zero));
        Log.Clock = clock;
        for (int i = 0; i < 25; i++)
        {
            Log.Warn("rollover repeat");
        }

        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        Log.Info("new day");

        string content = string.Join('\n', Directory.EnumerateFiles(_directory, "app-*.log").SelectMany(File.ReadAllLines));
        Assert.Contains("suppressed 5 repeats of: rollover repeat", content, StringComparison.Ordinal);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
