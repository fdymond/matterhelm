using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HtpcMatterBridge.Sidecar;

/// <summary>What to spawn: the node/SEA executable, its arguments, and the working directory.</summary>
public sealed record SidecarSpec(string ExePath, IReadOnlyList<string> Args, string WorkingDirectory);

/// <summary>
/// Supervisor timing knobs. The backoff constants deliberately mirror the
/// sidecar's own reconnect backoff (<c>bridge/src/ipc/client.ts</c>
/// DEFAULT_BACKOFF: base 500 ms doubling to a 30 s cap, ±25 % jitter) so both
/// halves of the link recover on the same schedule. Injectable so tests can
/// shrink them.
/// </summary>
public sealed record SupervisorOptions
{
    /// <summary>First restart delay in ms (doubles each attempt).</summary>
    public int BackoffBaseMs { get; init; } = 500;

    /// <summary>Restart delay ceiling in ms.</summary>
    public int BackoffCapMs { get; init; } = 30_000;

    /// <summary>A child surviving this long resets the backoff attempt counter.</summary>
    public int BackoffResetMs { get; init; } = 60_000;

    /// <summary>Grace period after closing the child's stdin before killing the process tree.</summary>
    public int StopGraceMs { get; init; } = 3_000;
}

/// <summary>
/// Spawns and babysits the sidecar process: per-session crypto-random IPC
/// token, environment contract (BLUEPRINT §2.3 table), stdout/stderr pumped
/// into the log (pino JSON lines mapped to levels), restart with jittered
/// exponential backoff on unexpected exit, and a stdin tether for shutdown.
///
/// Stdin tether: the child's stdin is kept open for its lifetime; on
/// <see cref="Stop"/> it is closed, and the sidecar (S1-5) watches for
/// stdin EOF to self-terminate. The supervisor still kills the process tree
/// after <see cref="SupervisorOptions.StopGraceMs"/> — the tether is
/// belt-and-suspenders, never the only cleanup path.
///
/// Threading: <see cref="ChildStarted"/>/<see cref="RestartScheduled"/> fire
/// on thread-pool threads (never under the internal lock) — marshalling to
/// the UI thread is the subscriber's job (S2-5). Event handlers and process
/// callbacks never let exceptions escape.
/// </summary>
public sealed class SidecarSupervisor : IDisposable
{
    private const double JitterFraction = 0.25;

    private readonly SidecarSpec _spec;
    private readonly int _ipcPort;
    private readonly string _storageDir;
    private readonly string _logLevel;
    private readonly SupervisorOptions _options;
    private readonly Action<string, string> _log;
    private readonly object _gate = new();
    private Process? _child;
    private System.Threading.Timer? _restartTimer;
    private int _attempt;
    private long _childStartedAt;
    private bool _started;
    private bool _stopping;

    /// <summary>Creates the supervisor and generates the session IPC token (nothing is spawned yet).</summary>
    /// <param name="spec">Executable, args, and working directory of the sidecar.</param>
    /// <param name="ipcPort">Port the tray app's <see cref="IpcServer"/> listens on.</param>
    /// <param name="storageDir">matter.js storage path handed to the child.</param>
    /// <param name="logLevel">pino level handed to the child.</param>
    /// <param name="options">Timing knobs; production uses the defaults.</param>
    /// <param name="log">Log sink (level, message); defaults to <see cref="Log"/>. Injectable for tests/demos.</param>
    public SidecarSupervisor(
        SidecarSpec spec,
        int ipcPort,
        string storageDir,
        string logLevel = "info",
        SupervisorOptions? options = null,
        Action<string, string>? log = null)
    {
        _spec = spec;
        _ipcPort = ipcPort;
        _storageDir = storageDir;
        _logLevel = logLevel;
        _options = options ?? new SupervisorOptions();
        _log = log ?? DefaultLog;
        IpcToken = GenerateToken();
    }

    /// <summary>
    /// Per-session auth token (32 crypto-random bytes, base64url), handed to
    /// the child via <c>HTPC_BRIDGE_IPC_TOKEN</c> and to <see cref="IpcServer"/>
    /// by the composition root. Never log or persist it.
    /// </summary>
    public string IpcToken { get; }

    /// <summary>A child process was (re)started. Fires on a pool thread.</summary>
    public event EventHandler? ChildStarted;

    /// <summary>A restart was scheduled after an unexpected exit; payload is the delay in ms. Fires on a pool thread.</summary>
    public event EventHandler<int>? RestartScheduled;

    /// <summary>
    /// Pure backoff schedule shared with the sidecar's client
    /// (<c>backoffDelayMs</c> in client.ts): <c>min(base * 2^attempt, cap)</c>
    /// scaled by a jitter factor in [0.75, 1.25] derived from
    /// <paramref name="unitRandom"/> in [0, 1).
    /// </summary>
    public static int ComputeBackoffDelayMs(int attempt, SupervisorOptions options, double unitRandom)
    {
        double ideal = Math.Min(options.BackoffCapMs, options.BackoffBaseMs * Math.Pow(2, attempt));
        double factor = 1 - JitterFraction + (unitRandom * 2 * JitterFraction);
        return (int)Math.Round(ideal * factor);
    }

    /// <summary>
    /// Maps one raw sidecar stdout line to a log level and message: pino JSON
    /// (<c>{"level":40,"msg":"…"}</c>, numeric or string level) maps
    /// fatal/error → ERROR and warn → WARN with the extracted <c>msg</c>;
    /// anything else passes through unchanged at INFO.
    /// </summary>
    public static (string Level, string Message) MapStdoutLine(string line)
    {
        string trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            return ("INFO", line);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(trimmed);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ("INFO", line);
            }

            string level = "INFO";
            if (root.TryGetProperty("level", out JsonElement levelElement))
            {
                level = MapPinoLevel(levelElement);
            }

            string message =
                root.TryGetProperty("msg", out JsonElement msgElement) && msgElement.ValueKind == JsonValueKind.String
                    ? msgElement.GetString()!
                    : line;
            return (level, message);
        }
        catch (JsonException)
        {
            return ("INFO", line);
        }
    }

    /// <summary>Spawns the child. May be called once; restarts are automatic.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                throw new InvalidOperationException("Start may only be called once.");
            }

            _started = true;
            StartChildLocked();
        }
    }

    /// <summary>
    /// Cancels any pending restart, closes the child's stdin (tether), and
    /// kills the process tree if it has not exited within the grace period.
    /// Idempotent; never throws.
    /// </summary>
    public void Stop()
    {
        Process? child;
        lock (_gate)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            _restartTimer?.Dispose();
            _restartTimer = null;
            child = _child;
            _child = null;
        }

        if (child is null)
        {
            _log("INFO", "sidecar supervisor stopped (no child running).");
            return;
        }

        try
        {
            bool exited;
            try
            {
                child.StandardInput.Close();
                exited = child.WaitForExit(_options.StopGraceMs);
            }
            catch (InvalidOperationException)
            {
                exited = true; // Already gone.
            }

            if (exited)
            {
                _log("INFO", "sidecar exited after stdin close (tether).");
            }
            else
            {
                _log("WARN", $"sidecar still running {_options.StopGraceMs} ms after stdin close; killing process tree.");
                child.Kill(entireProcessTree: true);
                child.WaitForExit(2_000);
            }
        }
        catch (Exception ex)
        {
            _log("ERROR", $"sidecar stop failed: {ex.Message}");
        }
        finally
        {
            child.Dispose();
        }
    }

    /// <summary>Equivalent to <see cref="Stop"/> — the supervisor must never outlive its child.</summary>
    public void Dispose() => Stop();

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string MapPinoLevel(JsonElement levelElement)
    {
        if (levelElement.ValueKind == JsonValueKind.Number && levelElement.TryGetInt32(out int numeric))
        {
            // pino numeric levels: 60 fatal, 50 error, 40 warn, 30 info, 20 debug, 10 trace.
            return numeric >= 50 ? "ERROR" : numeric >= 40 ? "WARN" : "INFO";
        }

        if (levelElement.ValueKind == JsonValueKind.String)
        {
            return levelElement.GetString() switch
            {
                "fatal" or "error" => "ERROR",
                "warn" => "WARN",
                _ => "INFO",
            };
        }

        return "INFO";
    }

    private static void DefaultLog(string level, string message)
    {
        switch (level)
        {
            case "ERROR":
                Log.Error(message);
                break;
            case "WARN":
                Log.Warn(message);
                break;
            default:
                Log.Info(message);
                break;
        }
    }

    /// <summary>Caller must hold <c>_gate</c>.</summary>
    private void StartChildLocked()
    {
        if (_stopping)
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _spec.ExePath,
            WorkingDirectory = _spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true, // Held open as the shutdown tether.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in _spec.Args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // Environment contract, BLUEPRINT §2.3 (fixed there so S1-5 reads the same names).
        startInfo.Environment["HTPC_BRIDGE_IPC_PORT"] = _ipcPort.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["HTPC_BRIDGE_IPC_TOKEN"] = IpcToken;
        startInfo.Environment["HTPC_BRIDGE_STORAGE_DIR"] = _storageDir;
        startInfo.Environment["HTPC_BRIDGE_LOG_LEVEL"] = _logLevel;

        var child = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        child.Exited += (_, _) => OnChildExited(child);
        child.OutputDataReceived += (_, e) => OnStdoutLine(e.Data);
        child.ErrorDataReceived += (_, e) => OnStderrLine(e.Data);
        try
        {
            child.Start();
        }
        catch (Exception ex)
        {
            child.Dispose();
            _log("ERROR", $"sidecar failed to start: {ex.Message}");
            ScheduleRestartLocked();
            return;
        }

        child.BeginOutputReadLine();
        child.BeginErrorReadLine();
        _child = child;
        _childStartedAt = Environment.TickCount64;
        _log("INFO", $"sidecar started (pid {child.Id}, attempt {_attempt}).");
        RaiseOnPool(() => ChildStarted?.Invoke(this, EventArgs.Empty));
    }

    private void OnChildExited(Process child)
    {
        try
        {
            // Parameterless WaitForExit drains the redirected-output readers
            // to EOF, so no tail lines are lost. Our children hold the only
            // handles to their pipes, so EOF follows exit promptly.
            child.WaitForExit();
            int exitCode = child.ExitCode;
            lock (_gate)
            {
                if (!ReferenceEquals(child, _child))
                {
                    return; // Stop() took ownership, or a stale handle.
                }

                _child = null;
                if (_stopping)
                {
                    return; // Stop() reports and disposes.
                }

                long uptimeMs = Environment.TickCount64 - _childStartedAt;
                if (uptimeMs >= _options.BackoffResetMs && _attempt > 0)
                {
                    _attempt = 0;
                    _log("INFO", $"sidecar ran {uptimeMs} ms; backoff reset.");
                }

                _log("WARN", $"sidecar exited unexpectedly (code {exitCode}) after {uptimeMs} ms.");
                ScheduleRestartLocked();
            }

            child.Dispose();
        }
        catch (Exception ex)
        {
            // During Stop() this handler can race the Process disposal; that
            // is expected shutdown noise, not an error worth reporting.
            lock (_gate)
            {
                if (_stopping)
                {
                    return;
                }
            }

            _log("ERROR", $"sidecar exit handling failed: {ex.Message}");
        }
    }

    /// <summary>Caller must hold <c>_gate</c>.</summary>
    private void ScheduleRestartLocked()
    {
        if (_stopping)
        {
            return;
        }

        int delayMs = ComputeBackoffDelayMs(_attempt, _options, Random.Shared.NextDouble());
        _attempt++;
        _log("INFO", $"sidecar restart in {delayMs} ms (attempt {_attempt}).");
        RaiseOnPool(() => RestartScheduled?.Invoke(this, delayMs));
        _restartTimer?.Dispose();
        _restartTimer = new System.Threading.Timer(_ => OnRestartDue(), null, delayMs, Timeout.Infinite);
    }

    private void OnRestartDue()
    {
        try
        {
            lock (_gate)
            {
                StartChildLocked();
            }
        }
        catch (Exception ex)
        {
            _log("ERROR", $"sidecar restart failed: {ex.Message}");
        }
    }

    private void OnStdoutLine(string? line)
    {
        if (line is null)
        {
            return; // Stream closed.
        }

        try
        {
            (string level, string message) = MapStdoutLine(line);
            _log(level, $"sidecar: {message}");
        }
        catch (Exception)
        {
            _log("INFO", $"sidecar: {line}");
        }
    }

    private void OnStderrLine(string? line)
    {
        if (line is null)
        {
            return;
        }

        _log("WARN", $"sidecar[stderr]: {line}");
    }

    /// <summary>Fires an event off the pool so subscribers never run under <c>_gate</c>; swallows subscriber exceptions.</summary>
    private void RaiseOnPool(Action fire)
    {
        _ = Task.Run(() =>
        {
            try
            {
                fire();
            }
            catch (Exception ex)
            {
                _log("ERROR", $"supervisor event subscriber threw: {ex}");
            }
        });
    }
}
