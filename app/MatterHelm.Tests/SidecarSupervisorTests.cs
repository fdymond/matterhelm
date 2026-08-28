using System.Security.Cryptography;
using System.Text;
using MatterHelm.Sidecar;
using Xunit;

namespace MatterHelm.Tests;

/// <summary>
/// Behaviour tests for <see cref="SidecarSupervisor"/>: the pure backoff
/// schedule (mirroring client.ts's backoffDelayMs cases), pino stdout mapping,
/// and real stub node children for spawn/env, crash-restart, backoff reset,
/// and the stdin tether. Timing-sensitive tests inject shrunken backoff
/// constants so the suite stays fast.
/// </summary>
public static class SidecarSupervisorTests
{
    // Child-process readiness/exit waits poll, so a generous ceiling costs
    // nothing on a fast machine and prevents flakes on loaded CI runners under
    // coverage instrumentation (0.4.4: a 10 s ceiling timed out on GitHub
    // Actions while passing locally 5/5). Negative assertions keep their own
    // fixed waits — raising those would only slow the suite.
    private static readonly TimeSpan ChildWaitTimeout = TimeSpan.FromSeconds(45);

    public sealed class BackoffSchedule
    {
        private static readonly SupervisorOptions _defaults = new();

        [Theory]
        [InlineData(0, 500)]
        [InlineData(1, 1_000)]
        [InlineData(2, 2_000)]
        public void DoublesFromTheBaseDelay(int attempt, int expected)
        {
            Assert.Equal(expected, SidecarSupervisor.ComputeBackoffDelayMs(attempt, _defaults, 0.5));
        }

        [Fact]
        public void CapsAt30Seconds()
        {
            Assert.Equal(30_000, SidecarSupervisor.ComputeBackoffDelayMs(20, _defaults, 0.5));
        }

        [Fact]
        public void JittersWithinPlusMinus25Percent()
        {
            Assert.Equal(375, SidecarSupervisor.ComputeBackoffDelayMs(0, _defaults, 0.0));
            Assert.Equal(625, SidecarSupervisor.ComputeBackoffDelayMs(0, _defaults, 1.0));
        }
    }

    public sealed class StdoutMapping
    {
        [Theory]
        [InlineData("""{"level":30,"msg":"listening"}""", "INFO", "listening")]
        [InlineData("""{"level":40,"msg":"socket down"}""", "WARN", "socket down")]
        [InlineData("""{"level":50,"msg":"boom"}""", "ERROR", "boom")]
        [InlineData("""{"level":60,"msg":"fatal boom"}""", "ERROR", "fatal boom")]
        [InlineData("""{"level":"warn","msg":"string level"}""", "WARN", "string level")]
        [InlineData("""{"level":"fatal","msg":"string level"}""", "ERROR", "string level")]
        public void MapsPinoJsonLinesToLevels(string line, string expectedLevel, string expectedMessage)
        {
            Assert.Equal((expectedLevel, expectedMessage), SidecarSupervisor.MapStdoutLine(line));
        }

        [Theory]
        [InlineData("plain text line")]
        [InlineData("{not actually json")]
        [InlineData("[1,2,3]")]
        public void PassesNonPinoLinesThroughAsRawInfo(string line)
        {
            Assert.Equal(("INFO", line), SidecarSupervisor.MapStdoutLine(line));
        }

        [Fact]
        public void FallsBackToTheRawLineWhenMsgIsMissing()
        {
            Assert.Equal(("WARN", """{"level":40}"""), SidecarSupervisor.MapStdoutLine("""{"level":40}"""));
        }
    }

    public sealed class TokenGeneration
    {
        [Fact]
        public void EachSessionGetsAFreshBase64UrlToken()
        {
            var spec = new SidecarSpec("unused.exe", [], Path.GetTempPath());
            using var first = new SidecarSupervisor(spec, ipcPort: 1, storageDir: "x");
            using var second = new SidecarSupervisor(spec, ipcPort: 1, storageDir: "x");
            Assert.NotEqual(first.IpcToken, second.IpcToken);
            Assert.Equal(43, first.IpcToken.Length); // 32 bytes -> 43 base64url chars, no padding.
            Assert.DoesNotContain('+', first.IpcToken);
            Assert.DoesNotContain('/', first.IpcToken);
            Assert.DoesNotContain('=', first.IpcToken);
        }
    }

    public sealed class ChildLifecycle
    {
        [Fact]
        public async Task SpawnSetsTheEnvironmentContractAndPumpsStdoutStderrIntoTheLog()
        {
            string node = TestSupport.RequireNodeExe();
            var capture = new TestSupport.LogCapture();
            string storageDir = Path.Combine(Path.GetTempPath(), "htpc-supervisor-test-storage");
            // The child reports a hash of the token it received, so the test
            // can verify exact delivery without the value ever entering logs.
            const string script =
                "const crypto = require('crypto');" +
                "const sha = crypto.createHash('sha256').update(process.env.HTPC_BRIDGE_IPC_TOKEN).digest('hex');" +
                "console.log('ENVCHECK port=' + process.env.HTPC_BRIDGE_IPC_PORT" +
                " + ' level=' + process.env.HTPC_BRIDGE_LOG_LEVEL" +
                " + ' storage=' + process.env.HTPC_BRIDGE_STORAGE_DIR" +
                " + ' tokenSha=' + sha);" +
                "console.log(JSON.stringify({ level: 40, msg: 'warn line' }));" +
                "console.log(JSON.stringify({ level: 50, msg: 'error line' }));" +
                "console.error('stderr line');" +
                "setInterval(() => {}, 1000);";
            using (var supervisor = new SidecarSupervisor(
                new SidecarSpec(node, ["-e", script], Path.GetTempPath()),
                ipcPort: 41234,
                storageDir: storageDir,
                logLevel: "debug",
                options: new SupervisorOptions { StopGraceMs = 250 },
                log: capture.Sink))
            {
                supervisor.Start();
                await TestSupport.WaitUntilAsync(
                    () => capture.ContainsMessage("stderr line"),
                    ChildWaitTimeout,
                    "child output to arrive");

                string expectedSha = Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(supervisor.IpcToken)))
                    .ToLowerInvariant();
                Assert.True(
                    capture.Contains(
                        "INFO",
                        $"ENVCHECK port=41234 level=debug storage={storageDir} tokenSha={expectedSha}"),
                    "child did not see the documented environment contract");
                Assert.True(capture.Contains("WARN", "sidecar: warn line"), "pino level 40 must map to WARN");
                Assert.True(capture.Contains("ERROR", "sidecar: error line"), "pino level 50 must map to ERROR");
                Assert.True(capture.Contains("WARN", "sidecar[stderr]: stderr line"), "stderr must map to WARN");
                Assert.False(capture.ContainsMessage(supervisor.IpcToken), "the token value must never be logged");
                supervisor.Stop();
            }
        }

        [Fact]
        public async Task ImmediateCrashRestartsWithGrowingJitteredDelays()
        {
            string node = TestSupport.RequireNodeExe();
            var capture = new TestSupport.LogCapture();
            var gate = new Lock();
            var delays = new List<int>();
            int starts = 0;
            using (var supervisor = new SidecarSupervisor(
                new SidecarSpec(node, ["-e", "process.exit(1);"], Path.GetTempPath()),
                ipcPort: 41234,
                storageDir: "x",
                options: new SupervisorOptions { BackoffBaseMs = 100, BackoffCapMs = 2_000, StopGraceMs = 250 },
                log: capture.Sink))
            {
                supervisor.ChildStarted += (_, _) =>
                {
                    lock (gate)
                    {
                        starts++;
                    }
                };
                supervisor.RestartScheduled += (_, delay) =>
                {
                    lock (gate)
                    {
                        delays.Add(delay);
                    }
                };
                supervisor.Start();
                await TestSupport.WaitUntilAsync(
                    () =>
                    {
                        lock (gate)
                        {
                            return delays.Count >= 3;
                        }
                    },
                    ChildWaitTimeout,
                    "three scheduled restarts");
                supervisor.Stop();
            }

            int[] observed;
            int startCount;
            lock (gate)
            {
                observed = [.. delays];
                startCount = starts;
            }

            Assert.True(startCount >= 3, $"expected at least 3 child starts, saw {startCount}");
            Assert.InRange(observed[0], 74, 126);   // 100 ms ± 25 % (± rounding)
            Assert.InRange(observed[1], 149, 251);  // 200 ms ± 25 %
            Assert.InRange(observed[2], 299, 501);  // 400 ms ± 25 %
            Assert.True(capture.Contains("WARN", "exited unexpectedly"), "the crash must be logged at WARN");
        }

        [Fact]
        public async Task BackoffResetsAfterAChildSurvivesTheHealthyThreshold()
        {
            string node = TestSupport.RequireNodeExe();
            var capture = new TestSupport.LogCapture();
            var gate = new Lock();
            var delays = new List<int>();
            string counterFile = Path.Combine(Path.GetTempPath(), $"htpc-backoff-reset-{Guid.NewGuid():N}.txt");
            // Runs 1 and 2 crash instantly (attempt climbs); run 3 survives
            // past BackoffResetMs, so the delay scheduled after ITS exit must
            // drop back to the base.
            const string script =
                "const fs = require('fs');" +
                "const file = process.argv[1];" +
                "let n = 0;" +
                "try { n = parseInt(fs.readFileSync(file, 'utf8'), 10) || 0; } catch {}" +
                "fs.writeFileSync(file, String(n + 1));" +
                "if (n < 2) process.exit(1);" +
                "setTimeout(() => process.exit(1), 500);";
            try
            {
                using var supervisor = new SidecarSupervisor(
                    new SidecarSpec(node, ["-e", script, counterFile], Path.GetTempPath()),
                    ipcPort: 41234,
                    storageDir: "x",
                    options: new SupervisorOptions
                    {
                        BackoffBaseMs = 100,
                        BackoffCapMs = 2_000,
                        BackoffResetMs = 400,
                        StopGraceMs = 250,
                    },
                    log: capture.Sink);
                supervisor.RestartScheduled += (_, delay) =>
                {
                    lock (gate)
                    {
                        delays.Add(delay);
                    }
                };
                supervisor.Start();
                await TestSupport.WaitUntilAsync(
                    () =>
                    {
                        lock (gate)
                        {
                            return delays.Count >= 3;
                        }
                    },
                    ChildWaitTimeout,
                    "three scheduled restarts");
                supervisor.Stop();
            }
            finally
            {
                File.Delete(counterFile);
            }

            int[] observed;
            lock (gate)
            {
                observed = [.. delays];
            }

            Assert.InRange(observed[0], 74, 126);   // attempt 0: 100 ms ± 25 %
            Assert.InRange(observed[1], 149, 251);  // attempt 1: 200 ms ± 25 %
            Assert.InRange(observed[2], 74, 126);   // healthy run reset the schedule to attempt 0
            Assert.True(observed[2] < observed[1], "the post-reset delay must drop below the previous one");
            Assert.True(capture.Contains("INFO", "backoff reset"), "the reset must be logged");
        }

        [Fact]
        public async Task StdinTetherStopsTheChildWithoutKilling()
        {
            string node = TestSupport.RequireNodeExe();
            var capture = new TestSupport.LogCapture();
            const string script =
                "process.stdin.resume();" +
                "process.stdin.on('end', () => process.exit(0));" +
                "console.log('tether-ready');" +
                "setInterval(() => {}, 1000);";
            using (var supervisor = new SidecarSupervisor(
                new SidecarSpec(node, ["-e", script], Path.GetTempPath()),
                ipcPort: 41234,
                storageDir: "x",
                log: capture.Sink))
            {
                supervisor.Start();
                await TestSupport.WaitUntilAsync(
                    () => capture.ContainsMessage("tether-ready"),
                    ChildWaitTimeout,
                    "child to signal readiness");
                supervisor.Stop(); // Default 3 s grace — the tether must beat it.
            }

            Assert.True(
                capture.Contains("INFO", "exited after stdin close (tether)"),
                "the child must exit via the stdin tether");
            Assert.False(capture.ContainsMessage("killing process tree"), "Kill must not have been needed");
        }

        [Fact]
        public async Task StopCancelsPendingRestartsAndIsIdempotent()
        {
            string node = TestSupport.RequireNodeExe();
            var capture = new TestSupport.LogCapture();
            var gate = new Lock();
            int starts = 0;
            int delayCount = 0;
            using (var supervisor = new SidecarSupervisor(
                new SidecarSpec(node, ["-e", "process.exit(1);"], Path.GetTempPath()),
                ipcPort: 41234,
                storageDir: "x",
                options: new SupervisorOptions { BackoffBaseMs = 400, BackoffCapMs = 2_000, StopGraceMs = 250 },
                log: capture.Sink))
            {
                supervisor.ChildStarted += (_, _) =>
                {
                    lock (gate)
                    {
                        starts++;
                    }
                };
                supervisor.RestartScheduled += (_, _) =>
                {
                    lock (gate)
                    {
                        delayCount++;
                    }
                };
                supervisor.Start();
                await TestSupport.WaitUntilAsync(
                    () =>
                    {
                        lock (gate)
                        {
                            return delayCount >= 1;
                        }
                    },
                    ChildWaitTimeout,
                    "a restart to be scheduled");
                supervisor.Stop();
                supervisor.Stop(); // Idempotent.
            }

            int startsAtStop;
            lock (gate)
            {
                startsAtStop = starts;
            }

            // The pending ~400 ms restart must never fire after Stop().
            await Task.Delay(800);
            lock (gate)
            {
                Assert.Equal(startsAtStop, starts);
            }
        }
    }
}
