using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using MatterHelm.Actions;
using MatterHelm.Diagnostics;
using MatterHelm.Sidecar;
using MatterHelm.Ui;

namespace MatterHelm;

/// <summary>
/// Executor seam <see cref="BridgeHost"/> wires against, so wiring tests can
/// substitute a fake without triggering real Windows side effects. The
/// production implementation is <see cref="ActionExecutorAdapter"/>;
/// <c>Actions/ActionExecutor.cs</c> itself is untouched by this seam.
/// </summary>
public interface IActionExecutor : IDisposable
{
    /// <summary>Raised on every system volume/mute change. May fire on any thread (production: an audio-service thread).</summary>
    event EventHandler<VolumeState>? VolumeChanged;

    /// <summary>Executes one named action (the protocol action names, plus <c>"sleep"</c> for the power mapping). Never throws; <c>false</c> = failure.</summary>
    bool Execute(string name, object? value = null);

    /// <summary>
    /// Executes display-off while retaining whether DDC/CI or Windows
    /// blanking actually completed. The default keeps existing test/external
    /// executors source-compatible but cannot identify their path.
    /// </summary>
    DisplayPowerOffResult ExecuteDisplaysOff() =>
        new(Execute("powerOff"), DisplayPowerOffPath.None);

    /// <summary>Clears any display-off keep-awake hold without waking the displays. Never throws.</summary>
    bool ReleaseDisplayKeepAwake();

    /// <summary>Reads the current volume/mute; throws <see cref="InvalidOperationException"/> when no audio endpoint exists.</summary>
    VolumeState GetVolumeState();
}

/// <summary>
/// Production <see cref="IActionExecutor"/>: forwards to
/// <see cref="ActionExecutor"/> and adds the one mapping it does not carry —
/// <c>"sleep"</c> (the <see cref="PowerOffAction.Sleep"/> power-off variant)
/// via the static <see cref="DisplayPower.Sleep"/> (no instance needed —
/// unlike <see cref="DisplayPower.DisplaysOff"/>, sleep has no window handle
/// to send a message to).
/// </summary>
public sealed class ActionExecutorAdapter : IActionExecutor
{
    private readonly ActionExecutor _executor = new();

    /// <inheritdoc />
    public event EventHandler<VolumeState>? VolumeChanged
    {
        add => _executor.Volume.VolumeChanged += value;
        remove => _executor.Volume.VolumeChanged -= value;
    }

    /// <inheritdoc />
    public bool Execute(string name, object? value = null) =>
        name == "sleep" ? DisplayPower.Sleep() : _executor.Execute(name, value);

    /// <inheritdoc />
    public DisplayPowerOffResult ExecuteDisplaysOff() => _executor.ExecuteDisplaysOff();

    /// <inheritdoc />
    public VolumeState GetVolumeState() => _executor.Volume.GetState();

    /// <inheritdoc />
    public bool ReleaseDisplayKeepAwake() => _executor.ReleaseDisplayKeepAwake();

    /// <inheritdoc />
    public void Dispose()
    {
        _executor.Dispose();
    }
}

/// <summary>
/// Production sidecar launch spec. S3-1 ships the Node SEA layout
/// <c>sidecar\bridge.exe</c> next to the exe (BLUEPRINT §2.6); the
/// pre-approved fallback layout <c>sidecar\node.exe sidecar\bridge.cjs</c>
/// (ADR-007 §2) is still honored so a hand-assembled dist works too.
/// Demos and tests always inject their own <see cref="SidecarSpec"/>.
/// </summary>
public static class SidecarLaunchSpec
{
    /// <summary>
    /// The packaged spec under <paramref name="baseDirectory"/>, or null when
    /// no packaged layout exists there. <c>sidecar\bridge.exe</c> (Node SEA,
    /// no args) is preferred; <c>sidecar\node.exe</c> +
    /// <c>sidecar\bridge.cjs</c> is the sanctioned fallback layout. The seam
    /// is a parameter (not <see cref="AppContext.BaseDirectory"/>) so tests
    /// can exercise the selection against a scratch directory.
    /// </summary>
    public static SidecarSpec? TryPackaged(string baseDirectory)
    {
        string packagedDir = Path.Combine(baseDirectory, "sidecar");
        string packagedSea = Path.Combine(packagedDir, "bridge.exe");
        if (File.Exists(packagedSea))
        {
            return new SidecarSpec(packagedSea, [], packagedDir);
        }

        string packagedNode = Path.Combine(packagedDir, "node.exe");
        string packagedEntry = Path.Combine(packagedDir, "bridge.cjs");
        if (File.Exists(packagedNode) && File.Exists(packagedEntry))
        {
            return new SidecarSpec(packagedNode, [packagedEntry], packagedDir);
        }

        return null;
    }

    /// <summary>
    /// The production spec, resolved relative to the exe; falls back to the
    /// repo build tree when the packaged layout is absent so the app is
    /// runnable during development. The dev fallback prefers a fresh
    /// <c>bridge/dist/bridge.cjs</c> esbuild bundle (S6-1: one node process,
    /// ~90 MB private and sub-second start vs three processes / ~160 MB /
    /// ~3 s under tsx); a stale or absent bundle falls back to running the
    /// sources via tsx, so development iteration never executes stale code.
    /// node.exe resolves via PATH — dev machines have it.
    /// </summary>
    public static SidecarSpec Default()
    {
        if (TryPackaged(AppContext.BaseDirectory) is SidecarSpec packaged)
        {
            return packaged;
        }

        // Dev fallback: walk up from the exe (bin\Release\net10.0-windows is
        // four levels below the repo root) looking for the bridge sources.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string bridgeDir = Path.Combine(dir.FullName, "bridge");
            string srcDir = Path.Combine(bridgeDir, "src");
            if (!File.Exists(Path.Combine(srcDir, "index.ts")))
            {
                continue;
            }

            string bundle = Path.Combine(bridgeDir, "dist", "bridge.cjs");
            if (IsBundleFresh(bundle, srcDir))
            {
                return new SidecarSpec("node.exe", [bundle], bridgeDir);
            }

            string tsxCli = Path.Combine(bridgeDir, "node_modules", "tsx", "dist", "cli.mjs");
            if (File.Exists(tsxCli))
            {
                return new SidecarSpec(
                    "node.exe", [tsxCli, Path.Combine(srcDir, "index.ts")], bridgeDir);
            }
        }

        // Neither layout found: return the (absent) packaged SEA spec; the
        // supervisor's spawn failure produces one clear ERROR instead of a
        // crash here.
        string packagedDir = Path.Combine(AppContext.BaseDirectory, "sidecar");
        return new SidecarSpec(Path.Combine(packagedDir, "bridge.exe"), [], packagedDir);
    }

    /// <summary>
    /// True iff <paramref name="bundlePath"/> exists and is at least as new
    /// as every file under <paramref name="srcDir"/> (recursive mtime check).
    /// The staleness guard for the dev bundle preference: after any source
    /// edit the bundle loses until <c>npm run bundle</c> re-produces it, so a
    /// dev loop that skips bundling silently keeps running current code via
    /// tsx instead of a stale bundle. Deliberately scoped to <c>src/</c> —
    /// a dependency bump (package.json) without a source change is not
    /// detected; re-run <c>npm run bundle</c> after <c>npm ci</c>.
    /// </summary>
    public static bool IsBundleFresh(string bundlePath, string srcDir)
    {
        if (!File.Exists(bundlePath))
        {
            return false;
        }

        DateTime bundleTime = File.GetLastWriteTimeUtc(bundlePath);
        if (!Directory.Exists(srcDir))
        {
            return true;
        }

        foreach (string file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTimeUtc(file) > bundleTime)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Outcome of <see cref="BridgeHost.FactoryReset"/>: <see cref="Ok"/> true
/// means the Matter storage directory is gone (whether or not it existed to
/// begin with); false means it is still there — <see cref="Error"/> carries
/// the last delete failure so the caller can show/log it. Never partial: the
/// directory is deleted wholesale via <see cref="Directory.Delete(string, bool)"/>
/// or not at all.
/// </summary>
public sealed record FactoryResetResult(bool Ok, string? Error);

/// <summary>Ordered, non-blocking dispatch for the host's otherwise blocking lifecycle operations.</summary>
internal sealed class SerialActionQueue
{
    private readonly Lock _gate = new();
    private readonly Action<Exception> _onError;
    private Task _tail = Task.CompletedTask;
    private bool _completed;

    internal SerialActionQueue(Action<Exception> onError) => _onError = onError;

    internal Task Enqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            if (_completed)
            {
                return Task.CompletedTask;
            }

            _tail = _tail.ContinueWith(
                _ => Execute(action),
                CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach,
                TaskScheduler.Default);
            return _tail;
        }
    }

    internal void Complete()
    {
        Task tail;
        lock (_gate)
        {
            _completed = true;
            tail = _tail;
        }

        tail.GetAwaiter().GetResult();
    }

    private void Execute(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _onError(ex);
        }
    }
}

/// <summary>One built-in endpoint's entry in the <c>HTPC_BRIDGE_ENDPOINTS</c> contract: display name + whether the bridge publishes it.</summary>
internal sealed record SidecarEndpointEntry(string Name, bool Enabled);

/// <summary>Power endpoint entry: irreversible actions opt into Off-to-On momentary reset.</summary>
internal sealed record SidecarPowerEndpointEntry(string Name, bool Enabled, bool Momentary);

/// <summary>One enabled custom endpoint's entry in <c>HTPC_BRIDGE_ENDPOINTS</c>: stable key + display name (never its action — the sidecar must not know what commands do).</summary>
internal sealed record SidecarCustomEndpointEntry(string Key, string Name, bool ResetAfterActivation);

/// <summary>
/// Wire shape of <c>HTPC_BRIDGE_ENDPOINTS</c> (BLUEPRINT §2.3 as amended by
/// ADR-004/ADR-012), serialized via <see cref="SidecarEnvJsonContext"/>; property
/// declaration order is the wire order.
/// </summary>
internal sealed record SidecarEndpointsEnv(
    SidecarEndpointEntry Speaker,
    SidecarEndpointEntry PlayPause,
    SidecarEndpointEntry Next,
    SidecarEndpointEntry Previous,
    SidecarPowerEndpointEntry Power,
    SidecarCustomEndpointEntry[] Custom);

/// <summary>
/// S2-5 composition root for the running bridge: owns the
/// <see cref="IpcServer"/> + <see cref="SidecarSupervisor"/> pair per enabled
/// session and wires action frames → <see cref="IActionExecutor"/> → overlay
/// flash → ack, pairing frames → <see cref="PairingReceived"/>, and the state
/// publisher (volume/mute on sidecar connect + on every change).
///
/// Tray-state derivation (<see cref="DeriveState"/>), from observable signals
/// only: <b>gray</b> (Disabled) = bridge not running; <b>green</b> (Connected)
/// = sidecar authenticated and Matter reports commissioned; <b>red</b> (Faulted) = ≥ 2
/// consecutive supervisor restarts since the last successful authentication (a
/// healthy sidecar authenticates within moments of starting, so repeated exits
/// without auth are a crash/restart loop), or the commissionable mDNS
/// advertisement is unobservable; <b>blue</b> (AwaitingPairing) = authenticated
/// and uncommissioned; <b>amber</b> (Running) = starting or awaiting status.
///
/// Reversible power modes are symmetric: each configured off action has its
/// own inverse on an On command. <see cref="PowerOffAction.PauseAndDisplaysOff"/> sends
/// dedicated Pause FIRST, then blanks displays — the pause lands while the player
/// is still visible/audible, and display-off is the terminal effect; both must
/// succeed for an ok ack. On reverses only its display half. Irreversible
/// modes dispatch only Off and are advertised to the sidecar as momentary.
///
/// Threading: <see cref="SetEnabled"/> and <see cref="FactoryReset"/> are
/// thread-safe and blocking (child stop grace, plus delete retries for the
/// latter) — call either from a worker thread, never a UI thread (shutdown
/// being the one sanctioned synchronous exception). IPC/supervisor callbacks run on
/// pool threads and never touch UI directly: <c>TrayContext</c>,
/// <c>OverlayHud</c>, and the pairing window all marshal internally, so
/// <see cref="StateChanged"/>/<see cref="PairingReceived"/>/the overlay sink
/// may be forwarded to them as-is. Volume-change publishing hops to the pool
/// so the audio-service callback thread is never blocked on the socket.
/// </summary>
public sealed class BridgeHost : IDisposable
{
    private const int FaultedRestartThreshold = 2;

    /// <summary>Volume delta for the custom volumeUp/volumeDown media-key actions (ADR-004 §1: ±5 %).</summary>
    private const int VolumeStepPercent = 5;

    // Volume echo dead-band (owner bug report): Google sets a level, Windows
    // snaps it to driver granularity, and echoing the ±1 % read-back made the
    // Home app rewrite its own slider ("76 % → 77 % bounce"). A read-back
    // within the dead-band of the value just commanded, inside a short
    // window, is quantization noise — Google already knows what it set.
    // Larger deltas (real local changes, media-key steps) still publish.
    private const int VolumeEchoDeadBandPercent = 1;
    private const int VolumeEchoWindowMilliseconds = 3000;

    // Publish debounce (owner bug report round 2): CoreAudio fires a change
    // callback for EVERY driver step during a Home-app slider drag; publishing
    // each one raced the command stream in both directions (tray → sidecar →
    // Matter attribute → hub → app) and read as lag + bouncing. Publishes now
    // coalesce: only the LATEST state after a quiet gap goes out, and the echo
    // dead-band is applied at flush time against the latest command.
    private const int VolumePublishDebounceMilliseconds = 250;

    // Factory reset (BLUEPRINT §2.5, S3-2): the sidecar's own storage-close
    // and this process's brief handle-flush window can leave the directory
    // locked for a moment right after the child exits. Retry briefly rather
    // than fail on the first attempt; give up (surfacing the failure) well
    // short of anything a user would call "hung".
    private static readonly TimeSpan FactoryResetDeleteRetryWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FactoryResetDeleteRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MacroShutdownWait = TimeSpan.FromSeconds(2);

    private readonly Config _config;
    private readonly IActionExecutor _executor;
    private readonly SidecarSpec _sidecarSpec;
    private readonly Action<OverlayContent>? _overlaySink;
    private readonly SupervisorOptions? _supervisorOptions;
    private readonly Action<string, string> _log;
    private readonly string _storageDir;

    /// <summary>Guards lifecycle (_server/_supervisor/_running/_disposed) and state-derivation fields; events always fire outside it.</summary>
    private readonly Lock _gate = new();

    /// <summary>Serializes each complete stop/start/reset/restart, including listener teardown outside <see cref="_gate"/>.</summary>
    private readonly Lock _lifecycleGate = new();
    private readonly SerialActionQueue _lifecycleQueue;

    private IpcServer? _server;
    private SidecarSupervisor? _supervisor;
    private bool _running;
    private bool _clientAuthenticated;
    private MatterStatusFrame? _matterStatus;
    private int _restartsSinceAuth;
    private PowerOffAction _sessionPowerOffAction;
    private BridgeState _state = BridgeState.Disabled;
    private bool _disposed;

    /// <summary>Cancels in-flight background-macro delay waits on dispose (S8-6), so app exit never waits out a macro.</summary>
    private readonly CancellationTokenSource _macroCts = new();
    private readonly Lock _macroGate = new();
    private readonly HashSet<Task> _macroTasks = [];
    private bool _macrosStopping;

    // Volume echo dead-band state (guarded by _gate; see the constants above).
    private int? _lastCommandedVolume;
    private bool _lastCommandedMuted;
    private long _lastVolumeCommandTicks;

    // Debounced-publish state (guarded by _publishGate; see the constants above).
    private readonly Lock _publishGate = new();
    private System.Threading.Timer? _publishTimer;
    private VolumeState? _pendingPublish;

    /// <summary>Serializes StateChanged delivery; see RecomputeState. Never taken while holding _gate.</summary>
    private readonly Lock _notifyGate = new();
    private BridgeState _notifiedState = BridgeState.Disabled;

    /// <summary>Creates the host (nothing starts until <see cref="SetEnabled"/>).</summary>
    /// <param name="config">Live config; sidecar-affecting settings are snapshotted at each enable, while <c>overlayEnabled</c> is read per action.</param>
    /// <param name="executor">Action executor seam; production passes <see cref="ActionExecutorAdapter"/>.</param>
    /// <param name="sidecarSpec">What to spawn; production passes <see cref="SidecarLaunchSpec.Default"/>, demos/tests inject a stub.</param>
    /// <param name="overlaySink">Overlay flash sink; production passes <c>OverlayHud.Show</c>. Only invoked while <c>overlayEnabled</c>. Volume-changing actions carry <see cref="OverlayContent.VolumePercent"/> so the HUD renders a fill bar (S4-5).</param>
    /// <param name="supervisorOptions">Supervisor timing knobs; production uses the defaults.</param>
    /// <param name="log">Log sink (level, message); defaults to <see cref="Log"/>. Injectable for tests/demos.</param>
    /// <param name="storageDir">matter.js storage dir handed to the sidecar; defaults to <c>%APPDATA%\MatterHelm\matter</c> (BLUEPRINT §2.5).</param>
    public BridgeHost(
        Config config,
        IActionExecutor executor,
        SidecarSpec sidecarSpec,
        Action<OverlayContent>? overlaySink = null,
        SupervisorOptions? supervisorOptions = null,
        Action<string, string>? log = null,
        string? storageDir = null)
    {
        _config = config;
        _executor = executor;
        _sidecarSpec = sidecarSpec;
        _overlaySink = overlaySink;
        _supervisorOptions = supervisorOptions;
        _log = log ?? DefaultLog;
        _storageDir = storageDir ?? Path.Combine(AppPaths.Root, "matter");
        _sessionPowerOffAction = config.Current.PowerOffAction;
        _lifecycleQueue = new SerialActionQueue(ex => _log("ERROR", $"bridge: lifecycle operation failed: {ex.Message}"));
        _executor.VolumeChanged += OnVolumeChanged;
        _config.Changed += OnConfigChanged;
    }

    /// <summary>The derived tray state changed. May fire on any thread; <c>TrayContext.SetState</c> marshals internally.</summary>
    public event EventHandler<BridgeState>? StateChanged;

    /// <summary>The sidecar sent commissioning payloads. May fire on a pool thread; <c>TrayContext.SetPairingInfo</c> marshals internally.</summary>
    public event EventHandler<PairingFrame>? PairingReceived;

    /// <summary>The current derived tray state.</summary>
    public BridgeState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    internal int RunningMacroCount
    {
        get
        {
            lock (_macroGate)
            {
                return _macroTasks.Count;
            }
        }
    }

    /// <summary>
    /// Pure tray-state rule (see the class doc for the rationale):
    /// not running → Disabled; commissioned/authenticated sidecar → Connected;
    /// missing advertisement → Faulted; uncommissioned → AwaitingPairing;
    /// ≥ <see cref="FaultedRestartThreshold"/> restarts since the last
    /// authentication → Faulted; otherwise → Running (amber).
    /// </summary>
    public static BridgeState DeriveState(
        bool running,
        bool clientAuthenticated,
        int restartsSinceAuth,
        MatterStatusFrame? matterStatus = null)
    {
        if (!running)
        {
            return BridgeState.Disabled;
        }

        if (clientAuthenticated)
        {
            return matterStatus switch
            {
                { Advertisement: AdvertisementStatus.Missing } => BridgeState.Faulted,
                { Commissioned: true } => BridgeState.Connected,
                { Commissioned: false } => BridgeState.AwaitingPairing,
                _ => BridgeState.Running,
            };
        }

        return restartsSinceAuth >= FaultedRestartThreshold ? BridgeState.Faulted : BridgeState.Running;
    }

    /// <summary>
    /// Starts (server first, then sidecar) or stops (sidecar first, then
    /// server — the child gets its stdin-tether shutdown while the socket it
    /// talks to still exists) the bridge. Idempotent and thread-safe.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        lock (_lifecycleGate)
        {
            SetEnabledCore(enabled);
        }
    }

    /// <summary>Queues an enable/disable request in caller-observed order without blocking the UI thread.</summary>
    internal Task QueueSetEnabled(bool enabled) => _lifecycleQueue.Enqueue(() => SetEnabled(enabled));

    /// <summary>Queues a factory reset and delivers its result on the queue worker.</summary>
    internal Task QueueFactoryReset(Action<FactoryResetResult> completed) =>
        _lifecycleQueue.Enqueue(() => completed(FactoryReset()));

    private void SetEnabledCore(bool enabled)
    {
        IpcServer? stoppingServer = null;
        SidecarSupervisor? stoppingSupervisor = null;
        bool releaseDisplayKeepAwake = false;
        bool startFailed = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (enabled == _running)
            {
                // A repeated disable is still a teardown boundary. The guard
                // release is idempotent and this closes the safety path even
                // if a prior stop only partially completed.
                releaseDisplayKeepAwake = !enabled;
            }
            else if (enabled)
            {
                if (!StartLocked())
                {
                    startFailed = true;
                }
            }
            else
            {
                releaseDisplayKeepAwake = true;
                stoppingSupervisor = _supervisor;
                stoppingServer = _server;
                _supervisor = null;
                _server = null;
                _running = false;
                _clientAuthenticated = false;
                _restartsSinceAuth = 0;
            }
        }

        if (releaseDisplayKeepAwake)
        {
            _ = _executor.ReleaseDisplayKeepAwake();
        }

        stoppingSupervisor?.Stop();
        stoppingServer?.Dispose();
        if (startFailed && _config.Current.BridgeEnabled)
        {
            _config.Current.BridgeEnabled = false;
            _ = _config.Save();
            _log("WARN", "bridge: start failed; persisted it disabled so the tray setting and host state remain consistent.");
        }

        RecomputeState();
    }

    /// <summary>Stops the bridge and detaches from the executor. Idempotent.</summary>
    public void Dispose()
    {
        _lifecycleQueue.Complete();
        Task[] macroTasks;
        lock (_lifecycleGate)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
            }

            lock (_macroGate)
            {
                _macrosStopping = true;
                macroTasks = [.. _macroTasks];
            }

            _macroCts.Cancel();
            SetEnabledCore(false);
            lock (_gate)
            {
                _disposed = true;
            }
        }

        if (macroTasks.Length > 0 && !Task.WaitAll(macroTasks, MacroShutdownWait))
        {
            _log(
                "WARN",
                $"bridge: {macroTasks.Count(task => !task.IsCompleted)} macro task(s) did not stop within {MacroShutdownWait.TotalSeconds:0.#} s; shutdown continues.");
        }

        _macroCts.Dispose();
        _config.Changed -= OnConfigChanged;
        _executor.VolumeChanged -= OnVolumeChanged;
        lock (_publishGate)
        {
            _publishTimer?.Dispose();
            _publishTimer = null;
            _pendingPublish = null;
        }
    }

    /// <summary>
    /// Unpair / factory-reset (BLUEPRINT §2.5): a blocking stop-delete-restart
    /// sequence, safe to call from the tray menu or the Settings → Advanced
    /// button. Order matters — the sidecar (and its open storage handles) is
    /// fully down <b>before</b> the directory is touched, so nothing races the
    /// delete:
    /// <list type="number">
    /// <item>Stop the bridge if running (same blocking path as <see cref="SetEnabled"/>).</item>
    /// <item>Delete the Matter storage directory (retrying briefly on a
    /// transient lock, see <see cref="FactoryResetDeleteRetryWindow"/>); a
    /// missing directory (never paired, or already reset) counts as success.
    /// Deletion is all-or-nothing — a failure never leaves a half-deleted
    /// directory, and is surfaced via the return value and a WARN log line,
    /// never swallowed.</item>
    /// <item>If the bridge was running before step 1, restart it. The
    /// sidecar's next boot finds no persisted fabric, so it generates a fresh
    /// commissioning identity (new pairing code) — that regeneration, not any
    /// special-cased "reset" path here, IS the re-pair flow (BLUEPRINT §2.5):
    /// the node keeps the same configured identity (name/port/endpoints),
    /// only the Google-side pairing is gone.</item>
    /// </list>
    /// On success, logs an INFO line and — matching every other user-visible
    /// bridge event — flashes the overlay (when enabled) so the user sees
    /// "open Pair with Google Home to re-pair" without having to check the
    /// log. Blocking (child stop grace + delete retries): call from a worker
    /// thread, never the UI thread — same rule as <see cref="SetEnabled"/>.
    /// </summary>
    public FactoryResetResult FactoryReset()
    {
        lock (_lifecycleGate)
        {
            return FactoryResetCore();
        }
    }

    private FactoryResetResult FactoryResetCore()
    {
        bool restoreEnabled = _config.Current.BridgeEnabled;
        SetEnabledCore(false);

        if (!TryDeleteStorageDirectory(_storageDir, out string? error))
        {
            _log(
                "WARN",
                $"bridge: factory reset could not delete Matter storage at '{_storageDir}' ({error}); "
                    + "nothing was partially deleted — the previous enabled state is being restored; retry once whatever "
                    + "holds the folder open (e.g. an antivirus scan or a slow-to-exit sidecar) has released it.");
            if (_config.Current.OverlayEnabled)
            {
                _overlaySink?.Invoke(new OverlayContent("Factory reset failed", error ?? "failed", IsError: true));
            }

            SetEnabledCore(restoreEnabled);
            if (restoreEnabled && State == BridgeState.Disabled)
            {
                _config.Current.BridgeEnabled = false;
                _ = _config.Save();
                _log("WARN", "bridge: factory-reset recovery could not restart the bridge; persisted it disabled so the tray and host remain consistent.");
            }

            return new FactoryResetResult(false, error);
        }

        // S10-8: always come back up, even if the bridge was off when the reset
        // ran. A factory reset exists only to re-pair, and an uncommissioned
        // node that isn't running advertises nothing — the Home app then fails
        // with "can't find device". Persisted so the tray tick and a later app
        // restart agree with what the bridge is actually doing.
        _config.Current.BridgeEnabled = true;
        _config.Save();
        SetEnabledCore(true);

        _log(
            "INFO",
            $"bridge: factory reset complete — Matter storage at '{_storageDir}' deleted; "
                + "bridge restarted and uncommissioned, a fresh pairing code follows in a few seconds.");
        if (_config.Current.OverlayEnabled)
        {
            _overlaySink?.Invoke(new OverlayContent(
                "Factory reset complete", "restarting — a new pairing code is coming", IsError: false));
        }

        return new FactoryResetResult(true, null);
    }

    /// <summary>
    /// Deletes <paramref name="dir"/> recursively, retrying on a transient
    /// lock for up to <see cref="FactoryResetDeleteRetryWindow"/>. A
    /// nonexistent directory is treated as already-deleted (true, no error).
    /// </summary>
    private static bool TryDeleteStorageDirectory(string dir, out string? error)
    {
        error = null;
        if (!Directory.Exists(dir))
        {
            return true;
        }

        long deadline = Environment.TickCount64 + (long)FactoryResetDeleteRetryWindow.TotalMilliseconds;
        while (true)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                if (Environment.TickCount64 >= deadline)
                {
                    return false;
                }

                Thread.Sleep(FactoryResetDeleteRetryDelay);
            }
        }
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
            case "DEBUG":
                Log.Debug(message);
                break;
            default:
                Log.Info(message);
                break;
        }
    }

    /// <summary>
    /// Non-core env vars for the sidecar child (BLUEPRINT §2.3 as amended by
    /// ADR-004/ADR-012): the endpoint contract as camelCase JSON — built-ins
    /// with name + enabled (Power also carries its derived momentary policy),
    /// enabled custom commands as key + name (disabled ones omitted; never
    /// their actions — the sidecar must not know what commands do) — and the
    /// optional mDNS interface pin.
    /// </summary>
    internal static Dictionary<string, string> BuildSidecarExtraEnv(BridgeConfig config)
    {
        CommandsConfig commands = config.Commands;
        var extra = new Dictionary<string, string>
        {
            ["HTPC_BRIDGE_ENDPOINTS"] = JsonSerializer.Serialize(
                new SidecarEndpointsEnv(
                    Speaker: new SidecarEndpointEntry(commands.Speaker.Name, commands.Speaker.Enabled),
                    PlayPause: new SidecarEndpointEntry(commands.PlayPause.Name, commands.PlayPause.Enabled),
                    Next: new SidecarEndpointEntry(commands.Next.Name, commands.Next.Enabled),
                    Previous: new SidecarEndpointEntry(commands.Previous.Name, commands.Previous.Enabled),
                    Power: new SidecarPowerEndpointEntry(
                        commands.Power.Name,
                        commands.Power.Enabled,
                        IsMomentaryPowerAction(config.PowerOffAction)),
                    Custom: [.. commands.Custom
                        .Where(c => c.Enabled)
                        .Select(c => new SidecarCustomEndpointEntry(c.Key, c.Name, c.ResetAfterActivation))]),
                SidecarEnvJsonContext.Default.SidecarEndpointsEnv),
            // Opt-in custom auto-reset window; Config guarantees 0–2000.
            ["HTPC_BRIDGE_MOMENTARY_RESET_MS"] = config.MomentaryResetMs.ToString(CultureInfo.InvariantCulture),

            // S10-4 commissioning identity. The seed is resolved once at
            // startup (MatterIdentity) and persisted, so it is always set by
            // the time the bridge starts; VID/PID are plain config values.
            ["HTPC_BRIDGE_VENDOR_ID"] = config.VendorId.ToString(CultureInfo.InvariantCulture),
            ["HTPC_BRIDGE_PRODUCT_ID"] = config.ProductId.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(config.UniqueIdSeed))
        {
            extra["HTPC_BRIDGE_UNIQUE_ID_SEED"] = config.UniqueIdSeed;
        }

        if (!string.IsNullOrWhiteSpace(config.BridgeName))
        {
            extra["HTPC_BRIDGE_NAME"] = config.BridgeName;
        }

        if (!string.IsNullOrWhiteSpace(config.MdnsInterface))
        {
            extra["HTPC_BRIDGE_MDNS_INTERFACE"] = config.MdnsInterface;
        }

        return extra;
    }

    /// <summary>Caller must hold <c>_gate</c>. Returns false (fully torn down, still disabled) when the port cannot be bound.</summary>
    private bool StartLocked()
    {
        BridgeConfig sessionConfig = _config.Current;
        int port = sessionConfig.IpcPort;
        _sessionPowerOffAction = sessionConfig.PowerOffAction;
        var supervisor = new SidecarSupervisor(
            _sidecarSpec, port, _storageDir, sessionConfig.LogLevel, _supervisorOptions, _log,
            BuildSidecarExtraEnv(sessionConfig));
        var server = new IpcServer(port, supervisor.IpcToken, log: _log);
        server.ActionReceived += OnActionReceived;
        server.PairingReceived += OnPairingReceived;
        server.MatterStatusReceived += OnMatterStatusReceived;
        server.ClientChanged += OnClientChanged;
        supervisor.RestartScheduled += OnRestartScheduled;

        try
        {
            server.Start();
        }
        catch (HttpListenerException ex)
        {
            _log("ERROR", $"bridge: cannot listen on port {port} ({ex.Message}); bridge stays disabled.");
            server.Dispose();
            supervisor.Dispose();
            return false;
        }

        supervisor.Start();
        _server = server;
        _supervisor = supervisor;
        _running = true;
        _clientAuthenticated = false;
        _matterStatus = null;
        _restartsSinceAuth = 0;
        _log("INFO", $"bridge: started (port {port}).");
        return true;
    }

    /// <summary>True iff <paramref name="server"/> is the live instance (guards against stragglers from a stopped session).</summary>
    private bool IsCurrent(IpcServer server)
    {
        lock (_gate)
        {
            return ReferenceEquals(server, _server);
        }
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        bool leftDisplayMode = IsDisplayPowerAction(e.OldConfig.PowerOffAction)
            && !IsDisplayPowerAction(e.NewConfig.PowerOffAction);
        if (leftDisplayMode)
        {
            _ = _executor.ReleaseDisplayKeepAwake();
            _log("INFO", "bridge: power-off behavior left a display mode; released any display keep-awake hold.");
        }

        if (!e.NewConfig.BridgeEnabled
            || !RequiresSidecarRestart(e.OldConfig, e.NewConfig))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || !_running)
            {
                return;
            }
        }

        _ = _lifecycleQueue.Enqueue(RestartAfterConfigChange);
    }

    private void RestartAfterConfigChange()
    {
        lock (_lifecycleGate)
        {
            RestartAfterConfigChangeCore();
        }
    }

    private void RestartAfterConfigChangeCore()
    {
        lock (_gate)
        {
            if (_disposed || !_running)
            {
                return;
            }
        }

        _log("INFO", "bridge: saved settings require a restart; restarting through the normal disable/enable path.");
        if (_config.Current.OverlayEnabled)
        {
            _overlaySink?.Invoke(new OverlayContent("Settings saved", "restarting bridge…", IsError: false));
        }

        SetEnabledCore(false);
        if (_config.Current.BridgeEnabled)
        {
            SetEnabledCore(true);
        }
    }

    private static bool IsDisplayPowerAction(PowerOffAction action) =>
        action is PowerOffAction.DisplaysOff or PowerOffAction.PauseAndDisplaysOff;

    /// <summary>True when the configured action can take the bridge offline before it can retain Off state.</summary>
    internal static bool IsMomentaryPowerAction(PowerOffAction action) => action switch
    {
        PowerOffAction.DisplaysOff or PowerOffAction.PauseAndDisplaysOff or PowerOffAction.Screensaver => false,
        PowerOffAction.Sleep => true,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    /// <summary>Includes the app-derived Power policy in the existing config restart decision.</summary>
    internal static bool RequiresSidecarRestart(BridgeConfig before, BridgeConfig after) =>
        before.PowerOffAction != after.PowerOffAction
        || SettingsViewModel.RequiresBridgeRestart(before, after);

    /// <summary>
    /// Runs on the IPC connection's serial action worker, leaving the receive
    /// loop free to validate and queue later frames. Execute, overlay, and ack
    /// remain ordered per action, and ack N precedes action N+1 processing.
    /// </summary>
    private void OnActionReceived(object? sender, ActionFrame frame)
    {
        if (sender is not IpcServer server || !IsCurrent(server))
        {
            return;
        }

        long receivedAt = Stopwatch.GetTimestamp();
        string intent = DescribeIntent(frame);
        bool ok;
        string pill;
        string? error;
        try
        {
            (ok, pill, error) = ExecuteFrame(frame);
        }
        catch (Exception ex)
        {
            // The executor contract is no-throw; a fake/adapter bug must still
            // nack rather than kill the receive loop with a dropped ack.
            _log("ERROR", $"bridge: action '{intent}' threw: {ex.Message}");
            (ok, pill, error) = (false, "failed", null);
        }

        long executedAt = Stopwatch.GetTimestamp();
        double executeMs = Stopwatch.GetElapsedTime(receivedAt, executedAt).TotalMilliseconds;
        AppMetrics.ActionExecuteMs.Record(executeMs);
        (ok ? AppMetrics.ActionsExecutedOk : AppMetrics.ActionsFailed).Add(1);

        (int? volumePercent, bool muted) = ok ? DescribeVolumeResult(frame) : (null, false);
        if (volumePercent is int commandedVolume)
        {
            // Arm the echo dead-band: the CoreAudio change callback for this
            // very command fires momentarily and must not bounce Google's UI.
            lock (_gate)
            {
                _lastCommandedVolume = commandedVolume;
                _lastCommandedMuted = muted;
                _lastVolumeCommandTicks = Environment.TickCount64;
            }
        }

        if (_config.Current.OverlayEnabled)
        {
            // Owner request: when the fill bar is showing the level, repeating
            // the percent on the primary line is redundant — volume sets read
            // "Google Home → Volume" and the bar carries the number. Acks and
            // logs keep the precise DescribeIntent text.
            string overlayIntent =
                frame is SetVolumeFrame && volumePercent is not null ? "Volume" : intent;
            _overlaySink?.Invoke(new OverlayContent($"Google Home → {overlayIntent}", ok ? pill : "failed", !ok)
            {
                VolumePercent = volumePercent,
                Muted = muted,
            });
        }

        // No apostrophes in the error: Utf8JsonWriter's default encoder emits
        // them as the escape sequence backslash-u0027 on the wire (correct
        // JSON, needlessly ugly).
        TrayFrame ack = ok
            ? new AckOkFrame(frame.Id)
            : new AckFailFrame(frame.Id, error ?? $"action failed: {intent}");
        try
        {
            if (server.SendAsync(ack).GetAwaiter().GetResult())
            {
                AppMetrics.AcksSent.Add(1);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Server stopped mid-send; the session is over anyway.
        }

        // ADR-006 §2: grep-friendly per-action timing at Debug; the action id
        // is the cross-process correlation key (the sidecar logs the same id).
        long ackedAt = Stopwatch.GetTimestamp();
        _log("DEBUG", string.Create(
            CultureInfo.InvariantCulture,
            $"IPC timing: {WireName(frame)} id={frame.Id} execute={executeMs:0.0}ms ack={Stopwatch.GetElapsedTime(executedAt, ackedAt).TotalMilliseconds:0.0}ms total={Stopwatch.GetElapsedTime(receivedAt, ackedAt).TotalMilliseconds:0.0}ms"));
    }

    /// <summary>The frame's wire action name (protocol.ts discriminators), for the grep-friendly timing line; custom actions carry their key.</summary>
    private static string WireName(ActionFrame frame) => frame switch
    {
        SetVolumeFrame => "setVolume",
        SetMutedFrame => "setMuted",
        CustomActionFrame custom => $"custom:{custom.Key}",
        BareActionFrame bare => bare.Name switch
        {
            BareActionName.PlayPause => "playPause",
            BareActionName.Play => "play",
            BareActionName.Pause => "pause",
            BareActionName.Next => "next",
            BareActionName.Previous => "previous",
            BareActionName.PowerOn => "powerOn",
            BareActionName.PowerOff => "powerOff",
            _ => throw new ArgumentOutOfRangeException(nameof(frame), bare.Name, null),
        },
        _ => frame.GetType().Name,
    };

    private void OnPairingReceived(object? sender, PairingFrame frame)
    {
        if (sender is not IpcServer server || !IsCurrent(server))
        {
            return;
        }

        _log("INFO", "bridge: pairing payload received from sidecar.");
        if (_config.Current.OverlayEnabled)
        {
            // BLUEPRINT §2.4: the HUD flashes on pairing events too.
            _overlaySink?.Invoke(new OverlayContent(
                "Google Home pairing", "pairing code ready — see Pair… in the tray menu", IsError: false));
        }

        PairingReceived?.Invoke(this, frame);
    }

    private void OnMatterStatusReceived(object? sender, MatterStatusFrame frame)
    {
        if (sender is not IpcServer server || !IsCurrent(server))
        {
            return;
        }

        lock (_gate)
        {
            _matterStatus = frame;
        }

        if (frame.Advertisement == AdvertisementStatus.Missing)
        {
            const string message = "bridge: commissionable mDNS advertisement is not observable; "
                + "Google Home cannot discover this bridge. Restart the bridge and check mDNS/firewall conflicts.";
            _log("ERROR", message);
            if (_config.Current.OverlayEnabled)
            {
                _overlaySink?.Invoke(new OverlayContent(
                    "Google Home pairing", "bridge advertisement is not visible — see the log", IsError: true));
            }
        }
        else
        {
            _log("INFO", $"bridge: Matter status commissioned={frame.Commissioned}, advertisement={frame.Advertisement}.");
        }

        RecomputeState();
    }

    private void OnClientChanged(object? sender, bool connected)
    {
        if (sender is not IpcServer server || !IsCurrent(server))
        {
            return;
        }

        HandleAuthenticatedClientChanged(connected, server);
    }

    /// <summary>Applies an authenticated-client transition after the event source has been validated.</summary>
    internal void HandleAuthenticatedClientChanged(bool connected, IpcServer? server = null)
    {

        lock (_gate)
        {
            _clientAuthenticated = connected;
            _matterStatus = null;
            if (connected)
            {
                _restartsSinceAuth = 0;
            }
        }

        if (connected)
        {
            // Synchronous on the connection thread so the on-connect snapshot
            // is the first outbound frame, before any acks.
            if (server is not null)
            {
                PublishStateSnapshot(server);
            }
        }
        else
        {
            _ = _executor.ReleaseDisplayKeepAwake();
        }

        RecomputeState();
    }

    private void OnRestartScheduled(object? sender, int delayMs)
    {
        bool current;
        lock (_gate)
        {
            current = ReferenceEquals(sender, _supervisor);
        }

        if (current)
        {
            HandleSupervisorRestartScheduled();
        }
    }

    /// <summary>Applies a validated supervisor-restart notification.</summary>
    internal void HandleSupervisorRestartScheduled()
    {
        lock (_gate)
        {
            _restartsSinceAuth++;
        }

        AppMetrics.SupervisorRestarts.Add(1);
        _ = _executor.ReleaseDisplayKeepAwake();
        RecomputeState();
    }

    /// <summary>Arrives on an audio-service thread — publish from the pool, never block the callback on the socket.</summary>
    private void OnVolumeChanged(object? sender, VolumeState state)
    {
        // Never blocks the audio-service callback thread: just coalesce the
        // latest state and (re)arm the quiet-gap timer.
        lock (_publishGate)
        {
            _pendingPublish = state;
            _publishTimer ??= new System.Threading.Timer(_ => FlushPendingPublish());
            _publishTimer.Change(VolumePublishDebounceMilliseconds, Timeout.Infinite);
        }
    }

    private void FlushPendingPublish()
    {
        VolumeState state;
        lock (_publishGate)
        {
            if (_pendingPublish is not VolumeState pending)
            {
                return;
            }

            state = pending;
            _pendingPublish = null;
        }

        IpcServer? server;
        lock (_gate)
        {
            server = _running ? _server : null;

            // Echo dead-band, applied to the settled value: a read-back within
            // ±1 % of the value a Google command just set (same mute state,
            // short window) is driver quantization noise — publishing it makes
            // the Home app bounce its own slider. Real changes exceed the band
            // or arrive after the window.
            if (server is not null
                && _lastCommandedVolume is int commanded
                && Environment.TickCount64 - _lastVolumeCommandTicks <= VolumeEchoWindowMilliseconds
                && Math.Abs(state.VolumePercent - commanded) <= VolumeEchoDeadBandPercent
                && state.Muted == _lastCommandedMuted)
            {
                AppMetrics.StateFramesSuppressed.Add(1);
                return;
            }
        }

        if (server is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (await server.SendAsync(new StateFrame(state.VolumePercent, state.Muted)).ConfigureAwait(false))
                {
                    AppMetrics.StateFramesPublished.Add(1);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // Server stopped mid-send; nothing left to notify.
            }
        });
    }

    private void PublishStateSnapshot(IpcServer server)
    {
        VolumeState state;
        try
        {
            state = _executor.GetVolumeState();
        }
        catch (InvalidOperationException ex)
        {
            _log("WARN", $"bridge: cannot publish state snapshot ({ex.Message}).");
            return;
        }

        try
        {
            if (server.SendAsync(new StateFrame(state.VolumePercent, state.Muted)).GetAwaiter().GetResult())
            {
                AppMetrics.StateFramesPublished.Add(1);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Server stopped mid-send; nothing left to notify.
        }
    }

    /// <summary>Executes one action frame: ok + success pill + optional failure detail for the ack (null = the generic "action failed: {intent}").</summary>
    private (bool Ok, string Pill, string? Error) ExecuteFrame(ActionFrame frame) => frame switch
    {
        SetVolumeFrame v => (_executor.Execute("setVolume", v.Value), $"volume set to {v.Value} %", null),
        SetMutedFrame m => (_executor.Execute("setMuted", m.Value), m.Value ? "muted" : "unmuted", null),
        BareActionFrame { Name: BareActionName.PlayPause } => (_executor.Execute("playPause"), "play/pause pressed", null),
        BareActionFrame { Name: BareActionName.Play } => (_executor.Execute("play"), "play", null),
        BareActionFrame { Name: BareActionName.Pause } => (_executor.Execute("pause"), "pause", null),
        BareActionFrame { Name: BareActionName.Next } => (_executor.Execute("next"), "next track", null),
        BareActionFrame { Name: BareActionName.Previous } => (_executor.Execute("previous"), "previous track", null),
        BareActionFrame { Name: BareActionName.PowerOn } => ExecutePowerAction(on: true),
        BareActionFrame { Name: BareActionName.PowerOff } => ExecutePowerAction(on: false),
        CustomActionFrame custom => ExecuteCustom(custom),
        _ => (false, "unknown action", null),
    };

    internal (bool Ok, string Pill, string? Error) ExecutePowerAction(bool on)
    {
        // A display-off hold belongs to the prior power-off action, not the
        // currently configured route. Always clear it on Power On, including
        // after the user changed the route to screensaver or sleep.
        bool released = !on || _executor.ReleaseDisplayKeepAwake();
        (bool ok, string pill) = RoutePowerAction(
            GetSessionPowerOffAction(),
            on,
            _executor.Execute,
            _executor.ExecuteDisplaysOff);
        return (released & ok, pill, null);
    }

    /// <summary>
    /// Routes a stateful power command to the configured concrete primitive.
    /// Kept free of IPC/native I/O so the complete action/direction matrix is
    /// specification-tested without blanking displays or suspending a machine.
    /// </summary>
    internal static (bool Ok, string Pill) RoutePowerAction(
        PowerOffAction action,
        bool on,
        Func<string, object?, bool> execute,
        Func<DisplayPowerOffResult>? executeDisplaysOff = null)
    {
        if (on)
        {
            return action switch
            {
                PowerOffAction.DisplaysOff or PowerOffAction.PauseAndDisplaysOff =>
                    (execute("powerOn", null), "displays woken"),
                PowerOffAction.Screensaver =>
                    (execute("stopScreenSaver", null), "screensaver dismissed"),
                PowerOffAction.Sleep => (true, "no action needed"),
                _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
            };
        }

        if (action == PowerOffAction.Screensaver)
        {
            return (execute("startScreenSaver", null), "screensaver started");
        }

        if (action == PowerOffAction.Sleep)
        {
            return (execute("sleep", null), "sleeping");
        }

        if (action is not (PowerOffAction.DisplaysOff or PowerOffAction.PauseAndDisplaysOff))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }

        // Pause first, then blank (see class doc). The displays-off delegate
        // still runs when pause fails so display-off remains the terminal effect.
        bool paused = action != PowerOffAction.PauseAndDisplaysOff || execute("pause", null);
        DisplayPowerOffResult displayResult = executeDisplaysOff?.Invoke()
            ?? new DisplayPowerOffResult(execute("powerOff", null), DisplayPowerOffPath.None);
        bool fallback = displayResult.Path == DisplayPowerOffPath.BlankingFallback;
        string pill = action == PowerOffAction.PauseAndDisplaysOff
            ? fallback ? "Paused + displays off — standby likely" : "paused + displays off"
            : fallback ? "Displays off — standby likely" : "displays off";
        return (paused & displayResult.Ok, pill);
    }

    /// <summary>
    /// Executes a v2 <c>custom</c> action: resolves the wire key against the
    /// enabled custom commands and dispatches its configured action through
    /// the executor seam. An unknown or disabled key nacks with a reason —
    /// the sidecar publishing an endpoint we no longer have is a config drift
    /// the ack should name, not a crash.
    /// </summary>
    private (bool Ok, string Pill, string? Error) ExecuteCustom(CustomActionFrame frame)
    {
        CustomCommandConfig? command = FindCustomCommand(frame.Key);
        if (command is null)
        {
            return (false, "failed", $"unknown or disabled custom command: {frame.Key}");
        }

        return ExecuteCustomAction(frame.Key, command.Action);
    }

    /// <summary>
    /// Executes one custom action — a command's own action or one sequence
    /// step (S8-3). Instant sequences (no delay steps) run inline on the IPC
    /// receive loop so their ack reports the real outcome; a sequence with
    /// delays is handed to a background macro runner instead (S8-6) — the
    /// receive loop is the WebSocket read loop, so blocking it would freeze
    /// EVERY later frame (a 10 s macro would stall volume commands behind it
    /// and hold app shutdown hostage). Its ack means "started"; the outcome
    /// arrives via log + overlay when the macro finishes.
    /// </summary>
    private (bool Ok, string Pill, string? Error) ExecuteCustomAction(string commandKey, CustomActionConfig action)
    {
        switch (action)
        {
            case MediaKeyActionConfig mediaKey:
                return ExecuteMediaKey(mediaKey.KeyName);
            case LaunchActionConfig launch:
            {
                // Distinct failure text (S9-5): a failed launch previously
                // surfaced its SUCCESS pill in the nack/macro error ("failed:
                // launched Spotify.exe") because the pill doubled as the
                // fallback error.
                string exeName = Path.GetFileName(launch.Path);
                bool launched = _executor.Execute("launch", new LaunchRequest(launch.Path, launch.Args));
                return launched
                    ? (true, $"launched {exeName}", null)
                    : (false, "failed", $"could not start {exeName} (see the app log)");
            }
            case KeySequenceActionConfig keySequence:
                return ExecuteKeySequence(commandKey, keySequence.Sequence);
            case SystemActionConfig system:
                return ExecuteSystemCommand(system.Command);
            case DelayActionConfig:
                return (false, "failed", $"delay is only valid inside a sequence: {commandKey}");
            case SequenceActionConfig sequence when sequence.Steps.Any(step => step is DelayActionConfig):
                StartBackgroundSequence(commandKey, sequence);
                return (true, $"running {sequence.Steps.Count} steps", null);
            case SequenceActionConfig sequence:
                return RunSequenceSteps(commandKey, sequence);
            default:
                return (false, "failed", $"unsupported action type for custom command: {commandKey}");
        }
    }

    /// <summary>Runs a sequence's steps in order, stopping at the first failure (error carries the 1-based step number).</summary>
    private (bool Ok, string Pill, string? Error) RunSequenceSteps(string commandKey, SequenceActionConfig sequence)
    {
        for (int i = 0; i < sequence.Steps.Count; i++)
        {
            if (sequence.Steps[i] is SequenceActionConfig)
            {
                // Config rejects nesting on load; reaching one here means
                // the config mutated since — nack, never recurse.
                return (false, "failed", $"custom command {commandKey}: step {i + 1} is a nested sequence");
            }

            (bool stepOk, string stepPill, string? stepError) = ExecuteCustomAction(commandKey, sequence.Steps[i]);
            if (!stepOk)
            {
                return (false, "failed", $"custom command {commandKey}: step {i + 1} of {sequence.Steps.Count} failed: {stepError ?? stepPill}");
            }
        }

        return (true, $"ran {sequence.Steps.Count} steps", null);
    }

    /// <summary>Runs a delay-bearing sequence without occupying a pool thread while it waits.</summary>
    private async Task<(bool Ok, string Pill, string? Error)> RunSequenceStepsAsync(
        string commandKey,
        SequenceActionConfig sequence,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < sequence.Steps.Count; i++)
        {
            CustomActionConfig step = sequence.Steps[i];
            if (step is SequenceActionConfig)
            {
                return (false, "failed", $"custom command {commandKey}: step {i + 1} is a nested sequence");
            }

            (bool stepOk, string stepPill, string? stepError) = step switch
            {
                DelayActionConfig delay => await WaitDelayStepAsync(delay.Ms, cancellationToken).ConfigureAwait(false),
                _ => ExecuteCustomAction(commandKey, step),
            };
            if (!stepOk)
            {
                return (false, "failed", $"custom command {commandKey}: step {i + 1} of {sequence.Steps.Count} failed: {stepError ?? stepPill}");
            }
        }

        return (true, $"ran {sequence.Steps.Count} steps", null);
    }

    private static async Task<(bool Ok, string Pill, string? Error)> WaitDelayStepAsync(
        int ms,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ms, cancellationToken).ConfigureAwait(false);
            return (true, $"waited {ms} ms", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (false, "failed", "macro cancelled (shutting down)");
        }
    }

    /// <summary>
    /// Runs a delay-bearing sequence on a worker thread (S8-6). Completion or
    /// failure is reported via log + overlay — the action's ack already went
    /// out as "started", because holding the WebSocket receive loop for up to
    /// 10 s of configured delays would stall every frame behind it.
    /// </summary>
    private void StartBackgroundSequence(string commandKey, SequenceActionConfig sequence)
    {
        string name = FindCustomCommand(commandKey)?.Name ?? commandKey;
        Task task;
        lock (_macroGate)
        {
            if (_macrosStopping)
            {
                return;
            }

            CancellationToken cancellationToken = _macroCts.Token;
            task = Task.Run(
                () => RunBackgroundSequenceAsync(commandKey, name, sequence, cancellationToken),
                CancellationToken.None);
            _macroTasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_macroGate)
                {
                    _macroTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunBackgroundSequenceAsync(
        string commandKey,
        string name,
        SequenceActionConfig sequence,
        CancellationToken cancellationToken)
    {
        try
        {
            (bool ok, string pill, string? error) =
                await RunSequenceStepsAsync(commandKey, sequence, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                _log("INFO", $"bridge: macro '{commandKey}' completed ({pill}).");
            }
            else
            {
                _log("ERROR", $"bridge: macro '{commandKey}' failed: {error ?? pill}");
            }

            if (_config.Current.OverlayEnabled)
            {
                _overlaySink?.Invoke(new OverlayContent($"Google Home → {name}", ok ? pill : "failed", !ok));
            }
        }
        catch (Exception ex)
        {
            // Executor contract is no-throw; this guard keeps a bug from
            // surfacing as an unobserved task exception.
            _log("ERROR", $"bridge: macro '{commandKey}' threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Executes a <c>keySequence</c> custom action (S7-1): re-parses the
    /// stored sequence (Config canonicalizes on load, so a miss here means
    /// the config mutated since — nack with a reason, never crash) and sends
    /// the chord through the executor seam.
    /// </summary>
    private (bool Ok, string Pill, string? Error) ExecuteKeySequence(string commandKey, string sequence)
    {
        if (!KeyChord.TryParse(sequence, out ParsedKeyChord? chord, out string? parseError))
        {
            return (false, "failed", $"invalid key sequence for custom command {commandKey}: {parseError}");
        }

        return (_executor.Execute("keySequence", chord), $"{chord.Canonical} sent", null);
    }

    /// <summary>
    /// Executes a <c>system</c> custom action (S8-5), mapping the configured
    /// command onto its executor verb. Sleep reuses the adapter's
    /// <c>"sleep"</c> mapping; displays reuse the power verbs.
    /// </summary>
    private (bool Ok, string Pill, string? Error) ExecuteSystemCommand(SystemCommandName command) => command switch
    {
        SystemCommandName.StartScreenSaver => (_executor.Execute("startScreenSaver"), "screensaver started", null),
        SystemCommandName.StopScreenSaver => (_executor.Execute("stopScreenSaver"), "screensaver dismissed", null),
        SystemCommandName.DisplaysOff => (_executor.Execute("powerOff"), "displays off", null),
        SystemCommandName.DisplaysOn => (_executor.Execute("powerOn"), "displays woken", null),
        SystemCommandName.Sleep => (_executor.Execute("sleep"), "sleeping", null),
        SystemCommandName.Hibernate => (_executor.Execute("hibernate"), "hibernating", null),
        SystemCommandName.Lock => (_executor.Execute("lock"), "workstation locked", null),
        SystemCommandName.CloseForegroundProgram => (_executor.Execute("closeForeground"), "close sent to focused program", null),
        SystemCommandName.Shutdown => (_executor.Execute("shutdown"), "shutting down", null),
        SystemCommandName.Restart => (_executor.Execute("restart"), "restarting", null),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };

    private (bool Ok, string Pill, string? Error) ExecuteMediaKey(MediaKeyName keyName) => keyName switch
    {
        MediaKeyName.PlayPause => (_executor.Execute("playPause"), "play/pause pressed", null),
        MediaKeyName.Next => (_executor.Execute("next"), "next track", null),
        MediaKeyName.Previous => (_executor.Execute("previous"), "previous track", null),
        MediaKeyName.Stop => (_executor.Execute("mediaStop"), "stop pressed", null),
        MediaKeyName.Mute => (_executor.Execute("muteToggle"), "mute toggled", null),
        MediaKeyName.VolumeUp => (_executor.Execute("volumeStep", VolumeStepPercent), $"volume up {VolumeStepPercent} %", null),
        MediaKeyName.VolumeDown => (_executor.Execute("volumeStep", -VolumeStepPercent), $"volume down {VolumeStepPercent} %", null),
        MediaKeyName.Play => (_executor.Execute("mediaPlay"), "play pressed", null),
        MediaKeyName.Pause => (_executor.Execute("mediaPause"), "pause pressed", null),
        _ => throw new ArgumentOutOfRangeException(nameof(keyName), keyName, null),
    };

    /// <summary>
    /// The resulting volume level (and muted flag) a successful
    /// <paramref name="frame"/> leaves behind, for the overlay's fill bar
    /// (S4-5): setVolume carries its own value; setMuted and the custom
    /// volumeUp/volumeDown media keys read the level back from the executor
    /// after execution. Null percent (non-volume action, or no audio endpoint
    /// to read back from) keeps the plain text pill.
    /// </summary>
    private (int? VolumePercent, bool Muted) DescribeVolumeResult(ActionFrame frame) => frame switch
    {
        SetVolumeFrame v => (v.Value, false),
        SetMutedFrame m => (ReadBackVolumePercent(), m.Value),
        CustomActionFrame custom when FindCustomCommand(custom.Key)?.Action is MediaKeyActionConfig
        {
            KeyName: MediaKeyName.VolumeUp or MediaKeyName.VolumeDown,
        } => (ReadBackVolumePercent(), false),
        _ => (null, false),
    };

    /// <summary>The executor's current volume, or null when no audio endpoint exists (the overlay then degrades to its text pill).</summary>
    private int? ReadBackVolumePercent()
    {
        try
        {
            return _executor.GetVolumeState().VolumePercent;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The enabled custom command with wire key <paramref name="key"/>, or null (a disabled command is deliberately not found — its endpoint should not exist).</summary>
    private CustomCommandConfig? FindCustomCommand(string key) =>
        _config.Current.Commands.Custom.FirstOrDefault(c => c.Enabled && c.Key == key);

    /// <summary>Human-readable command line for the overlay/ack ("volume 40 %", "mute", a custom command's display name, …).</summary>
    private string DescribeIntent(ActionFrame frame) => frame switch
    {
        SetVolumeFrame v => $"volume {v.Value} %",
        SetMutedFrame m => m.Value ? "mute" : "unmute",
        BareActionFrame { Name: BareActionName.PlayPause } => "play/pause",
        BareActionFrame { Name: BareActionName.Play } => "play",
        BareActionFrame { Name: BareActionName.Pause } => "pause",
        BareActionFrame { Name: BareActionName.Next } => "next track",
        BareActionFrame { Name: BareActionName.Previous } => "previous track",
        BareActionFrame { Name: BareActionName.PowerOn } => "power on",
        BareActionFrame { Name: BareActionName.PowerOff } => GetSessionPowerOffAction() switch
        {
            PowerOffAction.DisplaysOff => "power off (→ displays off)",
            PowerOffAction.Screensaver => "power off (→ screensaver)",
            PowerOffAction.Sleep => "power off (→ sleep)",
            _ => "power off (→ pause + displays off)",
        },
        // ADR-004: the overlay reads "Google Home → Movie Mode"; for an
        // unknown/disabled key the wire key is the only name there is.
        CustomActionFrame custom => FindCustomCommand(custom.Key)?.Name ?? custom.Key,
        _ => frame.GetType().Name,
    };

    private PowerOffAction GetSessionPowerOffAction()
    {
        lock (_gate)
        {
            return _sessionPowerOffAction;
        }
    }

    private void RecomputeState()
    {
        lock (_gate)
        {
            BridgeState derived = DeriveState(_running, _clientAuthenticated, _restartsSinceAuth, _matterStatus);
            if (derived == _state)
            {
                return;
            }

            _state = derived;
        }

        // Notifications are serialized under their own gate and always carry
        // the LATEST state, re-read after acquiring it: two threads racing
        // through the assignment above could otherwise deliver A-then-B while
        // the true state is A, pinning the tray icon on a stale color (S2-R
        // finding 2). A thread that arrives after its update was already
        // reported by a peer skips the duplicate. Handlers marshal via
        // BeginInvoke, so holding _notifyGate across Invoke cannot deadlock.
        lock (_notifyGate)
        {
            BridgeState latest;
            lock (_gate)
            {
                latest = _state;
            }

            if (latest == _notifiedState)
            {
                return;
            }

            _notifiedState = latest;
            StateChanged?.Invoke(this, latest);
        }
    }
}
