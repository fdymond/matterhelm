using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using HtpcMatterBridge.Actions;
using HtpcMatterBridge.Diagnostics;
using HtpcMatterBridge.Sidecar;
using HtpcMatterBridge.Ui;

namespace HtpcMatterBridge;

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

    /// <summary>Reads the current volume/mute; throws <see cref="InvalidOperationException"/> when no audio endpoint exists.</summary>
    VolumeState GetVolumeState();
}

/// <summary>
/// Production <see cref="IActionExecutor"/>: forwards to
/// <see cref="ActionExecutor"/> and adds the one mapping it does not carry —
/// <c>"sleep"</c> (the <see cref="PowerOffAction.Sleep"/> power-off variant)
/// via an owned <see cref="DisplayPower"/>.
/// </summary>
public sealed class ActionExecutorAdapter : IActionExecutor
{
    private readonly ActionExecutor _executor = new();
    private readonly DisplayPower _displayPower = new();

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
    public VolumeState GetVolumeState() => _executor.Volume.GetState();

    /// <inheritdoc />
    public void Dispose()
    {
        _executor.Dispose();
        _displayPower.Dispose();
    }
}

/// <summary>
/// Production sidecar launch spec. S3-1 (packaging) finalizes the shipped
/// layout; until then this is the fixed convention the packaging story will
/// satisfy: <c>sidecar\node.exe sidecar\bridge.cjs</c> next to the exe
/// (BLUEPRINT §2.6 — SEA exe or node-beside-bundle, either way one spec).
/// Demos and tests always inject their own <see cref="SidecarSpec"/>.
/// </summary>
public static class SidecarLaunchSpec
{
    /// <summary>
    /// The production spec, resolved relative to the exe; falls back to the
    /// repo build tree (bridge sources via tsx, node.exe from PATH) when the
    /// packaged layout is absent so the app is runnable during development.
    /// </summary>
    public static SidecarSpec Default()
    {
        string packagedDir = Path.Combine(AppContext.BaseDirectory, "sidecar");
        string packagedNode = Path.Combine(packagedDir, "node.exe");
        string packagedEntry = Path.Combine(packagedDir, "bridge.cjs");
        if (File.Exists(packagedNode) && File.Exists(packagedEntry))
        {
            return new SidecarSpec(packagedNode, [packagedEntry], packagedDir);
        }

        // Dev fallback: walk up from the exe (bin\Release\net8.0-windows is
        // four levels below the repo root) looking for the bridge sources +
        // installed tsx. node.exe resolves via PATH — dev machines have it.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string bridgeDir = Path.Combine(dir.FullName, "bridge");
            string tsxCli = Path.Combine(bridgeDir, "node_modules", "tsx", "dist", "cli.mjs");
            if (File.Exists(Path.Combine(bridgeDir, "src", "index.ts")) && File.Exists(tsxCli))
            {
                return new SidecarSpec(
                    "node.exe", [tsxCli, Path.Combine(bridgeDir, "src", "index.ts")], bridgeDir);
            }
        }

        // Neither layout found: return the packaged spec; the supervisor's
        // spawn failure produces one clear ERROR instead of a crash here.
        return new SidecarSpec(packagedNode, [packagedEntry], packagedDir);
    }
}

/// <summary>One built-in endpoint's entry in the <c>HTPC_BRIDGE_ENDPOINTS</c> contract: display name + whether the bridge publishes it.</summary>
internal sealed record SidecarEndpointEntry(string Name, bool Enabled);

/// <summary>One enabled custom endpoint's entry in <c>HTPC_BRIDGE_ENDPOINTS</c>: stable key + display name (never its action — the sidecar must not know what commands do).</summary>
internal sealed record SidecarCustomEndpointEntry(string Key, string Name);

/// <summary>
/// Wire shape of <c>HTPC_BRIDGE_ENDPOINTS</c> (BLUEPRINT §2.3 as amended by
/// ADR-004 §2), serialized via <see cref="SidecarEnvJsonContext"/>; property
/// declaration order is the wire order.
/// </summary>
internal sealed record SidecarEndpointsEnv(
    SidecarEndpointEntry Speaker,
    SidecarEndpointEntry PlayPause,
    SidecarEndpointEntry Next,
    SidecarEndpointEntry Previous,
    SidecarEndpointEntry Power,
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
/// = sidecar connected and token-authenticated; <b>red</b> (Faulted) = ≥ 2
/// consecutive supervisor restarts since the last successful authentication (a
/// healthy sidecar authenticates within moments of starting, so repeated exits
/// without auth are a crash/restart loop); <b>amber</b> (Running) = everything
/// else — running but not (yet) authenticated, including "pairing frame seen".
/// True commissioned-vs-uncommissioned detection needs a richer signal from
/// the sidecar (future additive protocol field; deliberately NOT added now),
/// so green means "sidecar link up", not "Google fabric joined".
///
/// power-off mapping: <see cref="PowerOffAction.PauseAndDisplaysOff"/> sends
/// play/pause FIRST, then blanks displays — the pause lands while the player
/// is still visible/audible, and display-off is the terminal effect; both must
/// succeed for an ok ack.
///
/// Threading: <see cref="SetEnabled"/> is thread-safe and blocking (child stop
/// grace) — call it from a worker thread, never a UI thread (shutdown being
/// the one sanctioned synchronous exception). IPC/supervisor callbacks run on
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

    private readonly Config _config;
    private readonly IActionExecutor _executor;
    private readonly SidecarSpec _sidecarSpec;
    private readonly Action<OverlayContent>? _overlaySink;
    private readonly SupervisorOptions? _supervisorOptions;
    private readonly Action<string, string> _log;
    private readonly string _storageDir;

    /// <summary>Guards lifecycle (_server/_supervisor/_running/_disposed) and state-derivation fields; events always fire outside it.</summary>
    private readonly Lock _gate = new();

    private IpcServer? _server;
    private SidecarSupervisor? _supervisor;
    private bool _running;
    private bool _clientAuthenticated;
    private int _restartsSinceAuth;
    private BridgeState _state = BridgeState.Disabled;
    private bool _disposed;

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
    /// <param name="config">Live config; <c>ipcPort</c>/<c>logLevel</c> are read at each enable, <c>overlayEnabled</c>/<c>powerOffAction</c> per action.</param>
    /// <param name="executor">Action executor seam; production passes <see cref="ActionExecutorAdapter"/>.</param>
    /// <param name="sidecarSpec">What to spawn; production passes <see cref="SidecarLaunchSpec.Default"/>, demos/tests inject a stub.</param>
    /// <param name="overlaySink">Overlay flash sink; production passes <c>OverlayHud.Show</c>. Only invoked while <c>overlayEnabled</c>. Volume-changing actions carry <see cref="OverlayContent.VolumePercent"/> so the HUD renders a fill bar (S4-5).</param>
    /// <param name="supervisorOptions">Supervisor timing knobs; production uses the defaults.</param>
    /// <param name="log">Log sink (level, message); defaults to <see cref="Log"/>. Injectable for tests/demos.</param>
    /// <param name="storageDir">matter.js storage dir handed to the sidecar; defaults to <c>%APPDATA%\HtpcMatterBridge\matter</c> (BLUEPRINT §2.5).</param>
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
        _storageDir = storageDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HtpcMatterBridge",
            "matter");
        _executor.VolumeChanged += OnVolumeChanged;
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

    /// <summary>
    /// Pure tray-state rule (see the class doc for the rationale):
    /// not running → Disabled; authenticated sidecar → Connected;
    /// ≥ <see cref="FaultedRestartThreshold"/> restarts since the last
    /// authentication → Faulted; otherwise → Running (amber).
    /// </summary>
    public static BridgeState DeriveState(bool running, bool clientAuthenticated, int restartsSinceAuth)
    {
        if (!running)
        {
            return BridgeState.Disabled;
        }

        if (clientAuthenticated)
        {
            return BridgeState.Connected;
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
        IpcServer? stoppingServer = null;
        SidecarSupervisor? stoppingSupervisor = null;
        lock (_gate)
        {
            if (_disposed || enabled == _running)
            {
                return;
            }

            if (enabled)
            {
                if (!StartLocked())
                {
                    return;
                }
            }
            else
            {
                stoppingSupervisor = _supervisor;
                stoppingServer = _server;
                _supervisor = null;
                _server = null;
                _running = false;
                _clientAuthenticated = false;
                _restartsSinceAuth = 0;
            }
        }

        stoppingSupervisor?.Stop();
        stoppingServer?.Dispose();
        RecomputeState();
    }

    /// <summary>Stops the bridge and detaches from the executor. Idempotent.</summary>
    public void Dispose()
    {
        SetEnabled(false);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _executor.VolumeChanged -= OnVolumeChanged;
        lock (_publishGate)
        {
            _publishTimer?.Dispose();
            _publishTimer = null;
            _pendingPublish = null;
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
    /// ADR-004 §2): the endpoint contract as camelCase JSON — built-ins with
    /// name + enabled (disabled ones present, the bridge omits the endpoint),
    /// enabled custom commands as key + name (disabled ones omitted; never
    /// their actions — the sidecar must not know what commands do) — and the
    /// optional mDNS interface pin.
    /// </summary>
    private static Dictionary<string, string> BuildSidecarExtraEnv(BridgeConfig config)
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
                    Power: new SidecarEndpointEntry(commands.Power.Name, commands.Power.Enabled),
                    Custom: [.. commands.Custom
                        .Where(c => c.Enabled)
                        .Select(c => new SidecarCustomEndpointEntry(c.Key, c.Name))]),
                SidecarEnvJsonContext.Default.SidecarEndpointsEnv),
        };
        if (!string.IsNullOrWhiteSpace(config.MdnsInterface))
        {
            extra["HTPC_BRIDGE_MDNS_INTERFACE"] = config.MdnsInterface;
        }

        return extra;
    }

    /// <summary>Caller must hold <c>_gate</c>. Returns false (fully torn down, still disabled) when the port cannot be bound.</summary>
    private bool StartLocked()
    {
        int port = _config.Current.IpcPort;
        var supervisor = new SidecarSupervisor(
            _sidecarSpec, port, _storageDir, _config.Current.LogLevel, _supervisorOptions, _log,
            BuildSidecarExtraEnv(_config.Current));
        var server = new IpcServer(port, supervisor.IpcToken, log: _log);
        server.ActionReceived += OnActionReceived;
        server.PairingReceived += OnPairingReceived;
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

    /// <summary>
    /// Runs on the IPC receive-loop thread (raised synchronously by
    /// <see cref="IpcServer"/>), so execute → overlay → ack stays ordered per
    /// action and ack N is sent before action N+1 is processed. Blocks only
    /// this pool thread, never the UI.
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

    private void OnClientChanged(object? sender, bool connected)
    {
        if (sender is not IpcServer server || !IsCurrent(server))
        {
            return;
        }

        lock (_gate)
        {
            _clientAuthenticated = connected;
            if (connected)
            {
                _restartsSinceAuth = 0;
            }
        }

        if (connected)
        {
            // Synchronous on the connection thread so the on-connect snapshot
            // is the first outbound frame, before any acks.
            PublishStateSnapshot(server);
        }

        RecomputeState();
    }

    private void OnRestartScheduled(object? sender, int delayMs)
    {
        bool current;
        lock (_gate)
        {
            current = ReferenceEquals(sender, _supervisor);
            if (current)
            {
                _restartsSinceAuth++;
            }
        }

        if (current)
        {
            AppMetrics.SupervisorRestarts.Add(1);
            RecomputeState();
        }
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
        BareActionFrame { Name: BareActionName.Next } => (_executor.Execute("next"), "next track", null),
        BareActionFrame { Name: BareActionName.Previous } => (_executor.Execute("previous"), "previous track", null),
        BareActionFrame { Name: BareActionName.PowerOn } => (_executor.Execute("powerOn"), "displays woken", null),
        BareActionFrame { Name: BareActionName.PowerOff } => ExecutePowerOff(),
        CustomActionFrame custom => ExecuteCustom(custom),
        _ => (false, "unknown action", null),
    };

    private (bool Ok, string Pill, string? Error) ExecutePowerOff() => _config.Current.PowerOffAction switch
    {
        PowerOffAction.DisplaysOff => (_executor.Execute("powerOff"), "displays off", null),
        PowerOffAction.Sleep => (_executor.Execute("sleep"), "sleeping", null),
        // Pause first, then blank (see class doc); non-short-circuit `&` so
        // the displays still go off even if the pause key injection failed.
        _ => (_executor.Execute("playPause") & _executor.Execute("powerOff"), "paused + displays off", null),
    };

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

        return command.Action switch
        {
            MediaKeyActionConfig mediaKey => ExecuteMediaKey(mediaKey.KeyName),
            LaunchActionConfig launch => (
                _executor.Execute("launch", new LaunchRequest(launch.Path, launch.Args)),
                $"launched {Path.GetFileName(launch.Path)}",
                null),
            _ => (false, "failed", $"unsupported action type for custom command: {frame.Key}"),
        };
    }

    private (bool Ok, string Pill, string? Error) ExecuteMediaKey(MediaKeyName keyName) => keyName switch
    {
        MediaKeyName.PlayPause => (_executor.Execute("playPause"), "play/pause pressed", null),
        MediaKeyName.Next => (_executor.Execute("next"), "next track", null),
        MediaKeyName.Previous => (_executor.Execute("previous"), "previous track", null),
        MediaKeyName.Stop => (_executor.Execute("mediaStop"), "stop pressed", null),
        MediaKeyName.Mute => (_executor.Execute("muteToggle"), "mute toggled", null),
        MediaKeyName.VolumeUp => (_executor.Execute("volumeStep", VolumeStepPercent), $"volume up {VolumeStepPercent} %", null),
        MediaKeyName.VolumeDown => (_executor.Execute("volumeStep", -VolumeStepPercent), $"volume down {VolumeStepPercent} %", null),
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
        BareActionFrame { Name: BareActionName.Next } => "next track",
        BareActionFrame { Name: BareActionName.Previous } => "previous track",
        BareActionFrame { Name: BareActionName.PowerOn } => "power on",
        BareActionFrame { Name: BareActionName.PowerOff } => _config.Current.PowerOffAction switch
        {
            PowerOffAction.DisplaysOff => "power off (→ displays off)",
            PowerOffAction.Sleep => "power off (→ sleep)",
            _ => "power off (→ pause + displays off)",
        },
        // ADR-004: the overlay reads "Google Home → Movie Mode"; for an
        // unknown/disabled key the wire key is the only name there is.
        CustomActionFrame custom => FindCustomCommand(custom.Key)?.Name ?? custom.Key,
        _ => frame.GetType().Name,
    };

    private void RecomputeState()
    {
        lock (_gate)
        {
            BridgeState derived = DeriveState(_running, _clientAuthenticated, _restartsSinceAuth);
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
