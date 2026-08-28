using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MatterHelm.Diagnostics;
using MatterHelm.Sidecar;
using Xunit;

namespace MatterHelm.Tests;

/// <summary>
/// Behaviour tests for the S5-2 diagnostics surface (ADR-006 §2): app log
/// level gating (Debug suppressed at the Info default, applied live), the
/// metrics JSON-lines snapshots (counters, histogram, retention), and the
/// diagnostics bundle (entry set, manifest facts, and the mandatory privacy
/// scan proving the live IPC token and username-bearing paths are absent).
/// Every test targets throwaway temp directories — never the real user
/// profile (CLAUDE.md ground rule 4).
/// </summary>
public static class DiagnosticsTests
{
    /// <summary>
    /// Serialization collection for tests that mutate the process-global
    /// <see cref="Log"/> state (directory/minimum level) — S5-R F4: running
    /// them in parallel with any future static-<see cref="Log"/> user races
    /// the redirected directory's recursive delete (the observed file-lock
    /// flake).
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class StaticLogState
    {
        /// <summary>Collection name for <see cref="CollectionAttribute"/> use.</summary>
        public const string Name = "static-log-state";
    }

    /// <summary>Redirects the static <see cref="Log"/> into a temp dir for the duration of one test, restoring the original directory and minimum level afterwards.</summary>
    [Collection(StaticLogState.Name)]
    public sealed class LogLevels : IDisposable
    {
        private readonly string _dir;
        private readonly string _originalDirectory;
        private readonly LogLevel _originalMinimum;
        private readonly TimeProvider _originalClock;
        private readonly long _originalFileSizeLimitBytes;

        public LogLevels()
        {
            _dir = Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _originalDirectory = Log.LogDirectory;
            _originalMinimum = Log.MinimumLevel;
            _originalClock = Log.Clock;
            _originalFileSizeLimitBytes = Log.FileSizeLimitBytes;
            Log.LogDirectory = _dir;
        }

        public void Dispose()
        {
            Log.LogDirectory = _originalDirectory;
            Log.MinimumLevel = _originalMinimum;
            Log.Clock = _originalClock;
            Log.FileSizeLimitBytes = _originalFileSizeLimitBytes;
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }

        private string ReadAll()
        {
            string path = Path.Combine(_dir, $"app-{DateTime.Now:yyyyMMdd}.log");
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        [Fact]
        public void DebugIsSuppressedAtTheInfoMinimumWhileInfoStillWrites()
        {
            Log.MinimumLevel = LogLevel.Info;

            Log.Debug("marker-debug-suppressed");
            Log.Info("marker-info-written");

            string content = ReadAll();
            Assert.DoesNotContain("marker-debug-suppressed", content, StringComparison.Ordinal);
            Assert.Contains("marker-info-written", content, StringComparison.Ordinal);
        }

        [Fact]
        public void DebugWritesWithItsOwnLabelOnceTheMinimumDropsToDebug()
        {
            Log.MinimumLevel = LogLevel.Debug;

            Log.Debug("marker-debug-written");

            Assert.Contains("[DEBUG] marker-debug-written", ReadAll(), StringComparison.Ordinal);
        }

        [Fact]
        public void ErrorMinimumSuppressesEverythingBelowError()
        {
            Log.MinimumLevel = LogLevel.Error;

            Log.Debug("marker-debug");
            Log.Info("marker-info");
            Log.Warn("marker-warn");
            Log.Error("marker-error");

            string content = ReadAll();
            Assert.DoesNotContain("marker-debug", content, StringComparison.Ordinal);
            Assert.DoesNotContain("marker-info", content, StringComparison.Ordinal);
            Assert.DoesNotContain("marker-warn", content, StringComparison.Ordinal);
            Assert.Contains("[ERROR] marker-error", content, StringComparison.Ordinal);
        }

        [Fact]
        public void MinimumLevelChangeAppliesToTheVeryNextWrite()
        {
            Log.MinimumLevel = LogLevel.Info;
            Log.Debug("marker-before-change");

            Log.MinimumLevel = LogLevel.Debug;
            Log.Debug("marker-after-change");

            string content = ReadAll();
            Assert.DoesNotContain("marker-before-change", content, StringComparison.Ordinal);
            Assert.Contains("marker-after-change", content, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("debug", LogLevel.Debug)]
        [InlineData("info", LogLevel.Info)]
        [InlineData("warn", LogLevel.Warn)]
        [InlineData("error", LogLevel.Error)]
        [InlineData("verbose", LogLevel.Info)] // unknown → Info (Config validates upstream)
        public void ParseLevelMapsTheConfigWireStrings(string wire, LogLevel expected)
        {
            Assert.Equal(expected, Log.ParseLevel(wire));
        }

        [Fact]
        public void DateRolloverPrunesExpiredFilesAndWritesTheNewDay()
        {
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            Log.Clock = clock;
            Log.Info("day one");
            string stale = Path.Combine(_dir, "app-20000101.log");
            File.WriteAllText(stale, "expired");
            File.SetLastWriteTimeUtc(stale, clock.GetUtcNow().UtcDateTime.AddDays(-30));

            clock.Advance(TimeSpan.FromDays(1));
            Log.Info("day two");

            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(Path.Combine(_dir, $"app-{clock.GetLocalNow():yyyyMMdd}.log")));
        }

        [Fact]
        public void FullDailyLogRollsToANumberedSegment()
        {
            Log.FileSizeLimitBytes = 1;

            Log.Info("first line exceeds the deliberately tiny test cap");
            Log.Info("second line must use a new segment");

            Assert.Equal(2, Directory.EnumerateFiles(_dir, "app-*.log").Count());
            Assert.True(File.Exists(Path.Combine(_dir, $"app-{DateTime.Now:yyyyMMdd}-001.log")));
        }
    }

    public sealed class MetricsSnapshots : IDisposable
    {
        private readonly string _dir;

        public MetricsSnapshots()
        {
            _dir = Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }

        [Fact]
        public void CounterIncrementsAndHistogramRecordsLandInTheJsonlSnapshot()
        {
            using (var listener = new MetricsFileListener(_dir, TimeSpan.FromHours(1)))
            {
                AppMetrics.ActionsExecutedOk.Add(3);
                AppMetrics.ActionsFailed.Add(1);
                AppMetrics.ActionExecuteMs.Record(1.5);
                AppMetrics.ActionExecuteMs.Record(2.5);
            } // dispose writes the final snapshot

            string path = Assert.Single(Directory.EnumerateFiles(_dir, "metrics-*.jsonl"));
            string line = File.ReadLines(path).Last(l => l.Length > 0);
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement counters = document.RootElement.GetProperty("counters");

            // ">=" everywhere: the counters are process-global and other test
            // classes drive real BridgeHost actions in parallel.
            Assert.True(counters.GetProperty("actions_executed_ok").GetInt64() >= 3);
            Assert.True(counters.GetProperty("actions_failed").GetInt64() >= 1);

            // Every ADR-006 §2 counter appears in the snapshot even untouched.
            foreach (string name in (string[])
            [
                "actions_executed_ok", "actions_failed", "acks_sent", "supervisor_restarts",
                "ipc_client_connects", "ipc_client_disconnects",
                "state_frames_published", "state_frames_suppressed",
            ])
            {
                Assert.True(counters.TryGetProperty(name, out _), $"counter {name} missing from the snapshot");
            }

            JsonElement histogram = document.RootElement.GetProperty("histograms").GetProperty("action_execute_ms");
            Assert.True(histogram.GetProperty("count").GetInt64() >= 2);
            Assert.True(histogram.GetProperty("max").GetDouble() >= 2.5);
            Assert.True(histogram.GetProperty("min").GetDouble() <= 1.5);
        }

        [Fact]
        public void SnapshotFilesOlderThanSevenDaysArePrunedOnConstruction()
        {
            string stale = Path.Combine(_dir, "metrics-20200101.jsonl");
            string fresh = Path.Combine(_dir, "metrics-fresh.jsonl");
            File.WriteAllText(stale, "{}");
            File.WriteAllText(fresh, "{}");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-30));

            using var listener = new MetricsFileListener(_dir, TimeSpan.FromHours(1));

            Assert.False(File.Exists(stale), "the 30-day-old snapshot file must be pruned");
            Assert.True(File.Exists(fresh), "recent snapshot files must survive the prune");
        }

        [Fact]
        public void DateRolloverPrunesExpiredSnapshotsWithoutRestartingTheListener()
        {
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            using var listener = new MetricsFileListener(_dir, TimeSpan.FromHours(1), 1024 * 1024, clock);
            listener.Flush();
            string stale = Path.Combine(_dir, "metrics-20000101.jsonl");
            File.WriteAllText(stale, "{}");
            File.SetLastWriteTimeUtc(stale, clock.GetUtcNow().UtcDateTime.AddDays(-30));

            clock.Advance(TimeSpan.FromDays(1));
            AppMetrics.ActionsExecutedOk.Add(1);
            listener.Flush();

            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(Path.Combine(_dir, $"metrics-{clock.GetLocalNow():yyyyMMdd}.jsonl")));
        }

        [Fact]
        public void FullSnapshotFileRollsToANumberedSegment()
        {
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            using var listener = new MetricsFileListener(_dir, TimeSpan.FromHours(1), 1, clock);
            listener.Flush();
            AppMetrics.ActionsExecutedOk.Add(1);
            listener.Flush();

            Assert.True(File.Exists(Path.Combine(_dir, $"metrics-{clock.GetLocalNow():yyyyMMdd}-001.jsonl")));
        }
    }

    /// <summary>
    /// S6-1 idle-churn rule: flushes whose counters/histograms are unchanged
    /// since the last written line are skipped. Runs in the serialized
    /// collection — <see cref="AppMetrics"/> is process-global, and a parallel
    /// test incrementing a counter between two "idle" flushes would make an
    /// intentionally-identical snapshot differ.
    /// </summary>
    [Collection(StaticLogState.Name)]
    public sealed class MetricsIdleChurn : IDisposable
    {
        private readonly string _dir;

        public MetricsIdleChurn()
        {
            _dir = Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }

        private static List<string> NonEmptyLines(string path) =>
            [.. File.ReadLines(path).Where(l => l.Length > 0)];

        [Fact]
        public void ResourceGaugesLandInEveryWrittenSnapshot()
        {
            // S9-6: memory/handle truth rides on each written line.
            using (var listener = new MetricsFileListener(_dir, TimeSpan.FromHours(1)))
            {
                listener.Flush();
            }

            string path = Assert.Single(Directory.EnumerateFiles(_dir, "metrics-*.jsonl"));
            string line = File.ReadLines(path).Last(l => l.Length > 0);
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement gauges = document.RootElement.GetProperty("gauges");

            // Real process values: all strictly positive.
            Assert.True(gauges.GetProperty("process_private_bytes").GetInt64() > 0);
            Assert.True(gauges.GetProperty("gc_heap_bytes").GetInt64() > 0);
            Assert.True(gauges.GetProperty("process_handle_count").GetInt64() > 0);
            Assert.True(gauges.GetProperty("process_thread_count").GetInt64() > 0);
        }

        [Fact]
        public void IdenticalIdleFlushesAreSkippedButAChangeStillAppends()
        {
            using var listener = new MetricsFileListener(_dir, TimeSpan.FromHours(1));
            listener.Flush(); // the day's first line — the file must exist
            listener.Flush(); // identical → skipped
            listener.Flush(); // identical → skipped

            string path = Assert.Single(Directory.EnumerateFiles(_dir, "metrics-*.jsonl"));
            string first = Assert.Single(NonEmptyLines(path));
            using (JsonDocument document = JsonDocument.Parse(first))
            {
                Assert.True(document.RootElement.TryGetProperty("ts", out _));
                Assert.True(document.RootElement.TryGetProperty("counters", out _));
                Assert.True(document.RootElement.TryGetProperty("histograms", out _));
            }

            AppMetrics.ActionsExecutedOk.Add(1);
            listener.Flush(); // changed → appended
            Assert.Equal(2, NonEmptyLines(path).Count);

            listener.Flush(); // identical again → skipped
            Assert.Equal(2, NonEmptyLines(path).Count);
        }

        [Fact]
        public void ADeletedFileIsRecreatedByTheNextFlushEvenWhenUnchanged()
        {
            using var listener = new MetricsFileListener(_dir, TimeSpan.FromHours(1));
            listener.Flush();
            string path = Assert.Single(Directory.EnumerateFiles(_dir, "metrics-*.jsonl"));

            File.Delete(path);
            listener.Flush(); // unchanged, but the file must come back

            Assert.True(File.Exists(path), "an unchanged flush must still recreate a missing snapshot file");
            _ = Assert.Single(NonEmptyLines(path));
        }
    }

    public sealed class Bundles : IDisposable
    {
        private readonly string _dir;
        private readonly string _logsDir;
        private readonly string _configPath;
        private readonly TestSupport.LogCapture _log = new();

        public Bundles()
        {
            _dir = Path.Combine(Path.GetTempPath(), "MatterHelmTests", Guid.NewGuid().ToString("N"));
            _logsDir = Path.Combine(_dir, "logs");
            _configPath = Path.Combine(_dir, "config.json");
            Directory.CreateDirectory(_logsDir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }

        private string ExportBundle()
        {
            _ = new Config(_configPath, _log.Sink); // writes the defaults file
            return DiagnosticsBundle.ExportTo(Path.Combine(_dir, "out", "diagnostics.zip"), _logsDir, _configPath);
        }

        private static string ReadEntryText(ZipArchiveEntry entry)
        {
            // Scan the raw bytes as UTF-8 — the token is base64url ASCII, so
            // a byte-level hit and a string-level hit are the same thing.
            using var buffer = new MemoryStream();
            using (Stream stream = entry.Open())
            {
                stream.CopyTo(buffer);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        [Fact]
        public void BundleContainsLogsMetricsManifestAndConfigAndNoEntryContainsTheIpcToken()
        {
            File.WriteAllLines(
                Path.Combine(_logsDir, "app-20260729.log"),
                [
                    "2026-07-29 10:00:00.000 [INFO] bridge: started (port 39531).",
                    "2026-07-29 10:00:01.000 [DEBUG] IPC timing: setVolume id=123e4567-e89b-12d3-a456-426614174000 execute=1.2ms ack=0.3ms total=1.5ms",
                    "2026-07-29 10:00:02.000 [INFO] IPC: sidecar connected and authenticated.",
                ]);
            File.WriteAllLines(
                Path.Combine(_logsDir, "metrics-20260729.jsonl"),
                ["""{"ts":"2026-07-29T10:01:00Z","counters":{"actions_executed_ok":1},"histograms":{}}"""]);

            // A never-started supervisor is enough: the per-session token
            // exists from construction (BLUEPRINT §2.3) and must be
            // unreachable from anything the bundle reads.
            using var supervisor = new SidecarSupervisor(
                new SidecarSpec("node.exe", [], _dir), 39531, Path.Combine(_dir, "matter"), log: _log.Sink);
            string token = supervisor.IpcToken;

            string zipPath = ExportBundle();

            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            Assert.Equal(
                ["config.json", "logs/app-20260729.log", "logs/metrics-20260729.jsonl", "manifest.json"],
                archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string content = ReadEntryText(entry);
                Assert.False(
                    content.Contains(token, StringComparison.Ordinal),
                    $"bundle entry {entry.FullName} contains the live IPC token");
            }
        }

        [Fact]
        public void BundledLogsAreScrubbedOfCommissioningCredentials()
        {
            // S5-R F1: matter.js's Commissioning facility logged the raw
            // setup passcode/manual code/QR at NOTICE, and such lines can sit
            // in logs written before the bridge-side suppression (or with a
            // user facility override). The bundle must scrub them regardless.
            File.WriteAllLines(
                Path.Combine(_logsDir, "app-20260729.log"),
                [
                    "2026-07-29 10:00:00.000 [INFO] sidecar: device is uncommissioned passcode: 74308742 discriminator: 2495 manual pairing code: 22368645352",
                    "2026-07-29 10:00:00.100 [INFO] sidecar: QR code URL: https://example.invalid/qrcode.html?data=MT:Y.K90SO527XL0V5PL10",
                    "2026-07-29 10:00:01.000 [INFO] bridge: pairing payload received from sidecar.",
                ]);

            string zipPath = ExportBundle();

            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            ZipArchiveEntry log = Assert.Single(archive.Entries, e => e.FullName == "logs/app-20260729.log");
            string content = ReadEntryText(log);

            Assert.DoesNotContain("74308742", content, StringComparison.Ordinal);
            Assert.DoesNotContain("22368645352", content, StringComparison.Ordinal);
            Assert.DoesNotContain("MT:Y.K90SO527XL0V5PL10", content, StringComparison.Ordinal);
            Assert.Contains("passcode: [redacted]", content, StringComparison.Ordinal);
            Assert.Contains("manual pairing code: [redacted]", content, StringComparison.Ordinal);
            Assert.Contains("MT:[redacted]", content, StringComparison.Ordinal);
            // Non-sensitive lines survive untouched.
            Assert.Contains("pairing payload received from sidecar.", content, StringComparison.Ordinal);
        }

        [Fact]
        public void ManifestCarriesEnvironmentFactsButNoUsernameMachineNameOrUserPaths()
        {
            string zipPath = ExportBundle();

            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            ZipArchiveEntry manifest = Assert.Single(archive.Entries, e => e.FullName == "manifest.json");
            string text = ReadEntryText(manifest);

            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            Assert.False(string.IsNullOrEmpty(root.GetProperty("os").GetString()));
            Assert.False(string.IsNullOrEmpty(root.GetProperty("dotnetRuntime").GetString()));
            Assert.False(string.IsNullOrEmpty(root.GetProperty("appVersion").GetString()));
            Assert.False(string.IsNullOrEmpty(root.GetProperty("locale").GetString()));
            Assert.True(root.TryGetProperty("generatedUtc", out _));
            Assert.True(root.TryGetProperty("sidecarVersion", out _));

            Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                Path.Combine(@"C:\Users", Environment.UserName), text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
