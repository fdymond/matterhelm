using System.Diagnostics;
using System.Globalization;
using MatterHelm.Actions;
using MatterHelm.Diagnostics;
using MatterHelm.Sidecar;
using MatterHelm.Ui;

namespace MatterHelm;

/// <summary>
/// Executor seam <see cref="BridgeHost"/> wires against, so wiring tests can
/// substitute a fake without triggering real Windows side effects. The
/// production implementation is <see cref="ActionExecutorAdapter"/>, including
/// lifecycle invalidation for display and screensaver state.
/// </summary>
public interface IActionExecutor : IDisposable
{
    /// <summary>Raised on every system volume/mute change. May fire on any thread (production: an audio-service thread).</summary>
    event EventHandler<VolumeState>? VolumeChanged;

    /// <summary>Executes one named action (the protocol action names, plus <c>"sleep"</c> for the power mapping). Never throws; <c>false</c> = failure.</summary>
    bool Execute(string name, object? value = null);

    /// <summary>Executes one action while preserving a truthful failure reason for an ack or macro step.</summary>
    ActionExecutionResult ExecuteDetailed(string name, object? value = null)
    {
        bool ok = Execute(name, value);
        return ok
            ? ActionExecutionResult.Success
            : ActionExecutionResult.Failure($"action '{name}' failed (see the app log)");
    }

    /// <summary>
    /// Executes display-off while retaining whether DDC/CI or Windows
    /// blanking actually completed. The default keeps existing test/external
    /// executors source-compatible but cannot identify their path.
    /// </summary>
    DisplayPowerOffResult ExecuteDisplaysOff() =>
        new(Execute("powerOff"), DisplayPowerOffPath.None);

    /// <summary>Clears any display-off keep-awake hold without waking the displays. Never throws.</summary>
    bool ReleaseDisplayKeepAwake();

    /// <summary>Invalidates retained pointer captures that no longer belong to enabled mouse commands.</summary>
    void ReconcileMouseMoves(IReadOnlySet<string> activeCommandKeys)
    {
    }

    /// <summary>Invalidates any foreground-window capture retained for screensaver dismissal.</summary>
    void ClearScreensaverFocusCapture()
    {
    }

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
    public ActionExecutionResult ExecuteDetailed(string name, object? value = null) =>
        name == "sleep"
            ? Execute(name, value)
                ? ActionExecutionResult.Success
                : ActionExecutionResult.Failure("Windows refused the sleep request (see the app log)")
            : _executor.ExecuteDetailed(name, value);

    /// <inheritdoc />
    public DisplayPowerOffResult ExecuteDisplaysOff() => _executor.ExecuteDisplaysOff();

    /// <inheritdoc />
    public VolumeState GetVolumeState() => _executor.Volume.GetState();

    /// <inheritdoc />
    public bool ReleaseDisplayKeepAwake() => _executor.ReleaseDisplayKeepAwake();

    /// <inheritdoc />
    public void ReconcileMouseMoves(IReadOnlySet<string> activeCommandKeys) =>
        _executor.ReconcileMouseMoves(activeCommandKeys);

    /// <inheritdoc />
    public void ClearScreensaverFocusCapture() => _executor.ClearScreensaverFocusCapture();

    /// <inheritdoc />
    public void Dispose()
    {
        _executor.Dispose();
    }
}

/// <summary>
/// Outcome of <see cref="BridgeHost.FactoryReset"/>: <see cref="Ok"/> true
/// means the live Matter storage directory is gone (whether it was deleted or
/// atomically moved aside for cleanup); false means staging failed and the live
/// directory was left untouched. <see cref="Error"/> describes that staging
/// failure; cleanup residue after a completed reset is logged separately.
/// </summary>
public sealed record FactoryResetResult(bool Ok, string? Error);

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
    private readonly Config _config;
    private readonly IActionExecutor _executor;
    private readonly Action<OverlayContent>? _overlaySink;
    private readonly Action<string, string> _log;
    private readonly VolumeStatePublisher _volumePublisher;
    private readonly BridgeActionDispatcher _actionDispatcher;
    private readonly BridgeLifecycleCoordinator _lifecycle;
    private readonly Lock _disposeGate = new();
    private bool _disposed;

    /// <summary>Creates the host (nothing starts until <see cref="SetEnabled"/>).</summary>
    /// <param name="config">Live bridge configuration.</param>
    /// <param name="executor">Action executor seam.</param>
    /// <param name="sidecarSpec">The sidecar process to supervise.</param>
    /// <param name="overlaySink">Optional overlay flash sink.</param>
    /// <param name="supervisorOptions">Optional supervisor timing overrides.</param>
    /// <param name="log">Optional log sink.</param>
    /// <param name="storageDir">Matter storage directory.</param>
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
        _overlaySink = overlaySink;
        _log = log ?? Log.Write;
        _volumePublisher = new VolumeStatePublisher(_executor.GetVolumeState, TimeProvider.System, _log);
        _actionDispatcher = new BridgeActionDispatcher(_config, _executor, _overlaySink, _log);
        _lifecycle = new BridgeLifecycleCoordinator(
            _config,
            _executor,
            sidecarSpec,
            _overlaySink,
            supervisorOptions,
            _log,
            storageDir ?? Path.Combine(AppPaths.Root, "matter"),
            _volumePublisher,
            _actionDispatcher);
        _lifecycle.ActionReceived += OnActionReceived;
        _lifecycle.StateChanged += OnStateChanged;
        _lifecycle.PairingReceived += OnPairingReceived;
        _executor.VolumeChanged += _volumePublisher.OnVolumeChanged;
    }

    /// <summary>The derived tray state changed.</summary>
    public event EventHandler<BridgeState>? StateChanged;

    /// <summary>The sidecar sent commissioning payloads.</summary>
    public event EventHandler<PairingFrame>? PairingReceived;

    /// <summary>The current derived tray state.</summary>
    public BridgeState State => _lifecycle.State;

    internal int RunningMacroCount => _actionDispatcher.RunningMacroCount;

    /// <summary>Maps observable lifecycle signals to a tray state.</summary>
    public static BridgeState DeriveState(
        bool running,
        bool clientAuthenticated,
        int restartsSinceAuth,
        MatterStatusFrame? matterStatus = null) =>
        BridgeLifecycleCoordinator.DeriveState(
            running,
            clientAuthenticated,
            restartsSinceAuth,
            matterStatus);

    /// <summary>Starts or stops the bridge through its serialized lifecycle path.</summary>
    public void SetEnabled(bool enabled) => _lifecycle.SetEnabled(enabled);

    internal Task QueueSetEnabledAsync(bool enabled) => _lifecycle.QueueSetEnabledAsync(enabled);

    internal Task QueueFactoryResetAsync(Action<FactoryResetResult> completed) =>
        _lifecycle.QueueFactoryResetAsync(completed);

    /// <summary>
    /// Stops the active session, atomically moves live Matter storage aside,
    /// removes it, and starts an uncommissioned session.
    /// </summary>
    public FactoryResetResult FactoryReset() => _lifecycle.FactoryReset();

    /// <summary>Stops the bridge and detaches its collaborators. Idempotent.</summary>
    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _lifecycle.Dispose();
        _lifecycle.ActionReceived -= OnActionReceived;
        _lifecycle.StateChanged -= OnStateChanged;
        _lifecycle.PairingReceived -= OnPairingReceived;
        _actionDispatcher.Dispose();
        _executor.VolumeChanged -= _volumePublisher.OnVolumeChanged;
        _volumePublisher.Dispose();
    }

    internal static bool RequiresSidecarRestart(BridgeConfig before, BridgeConfig after) =>
        BridgeRestartPolicy.RequiresRestart(before, after);

    internal static Dictionary<string, string> BuildSidecarExtraEnv(BridgeConfig config) =>
        SidecarEnvironment.BuildExtraEnv(config);

    internal static (bool Ok, string Pill) RoutePowerAction(
        PowerOffAction action,
        bool on,
        Func<string, object?, bool> execute,
        Func<DisplayPowerOffResult>? executeDisplaysOff = null) =>
        BridgeActionDispatcher.RoutePowerAction(action, on, execute, executeDisplaysOff);

    internal static (bool Ok, string Pill, string? Error) RunSequenceSteps(
        string commandKey,
        SequenceActionConfig sequence,
        Func<CustomActionConfig, (bool Ok, string Pill, string? Error)> executeStep) =>
        BridgeActionDispatcher.RunSequenceSteps(commandKey, sequence, executeStep);

    internal void HandleAuthenticatedClientChanged(bool connected, IpcServer? server = null) =>
        _lifecycle.HandleAuthenticatedClientChanged(connected, server);

    internal void HandleSupervisorRestartScheduled() =>
        _lifecycle.HandleSupervisorRestartScheduled();

    internal (bool Ok, string Pill, string? Error) ExecuteFrame(ActionFrame frame) =>
        _actionDispatcher.ExecuteFrame(frame);

    internal (bool Ok, string Pill, string? Error) ExecutePowerAction(bool on) =>
        _actionDispatcher.ExecutePowerAction(on);

    internal void BeginMacroSession() => _actionDispatcher.BeginMacroSession();

    private void OnActionReceived(IpcServer server, ActionFrame frame)
    {
        long receivedAt = Stopwatch.GetTimestamp();
        string intent = _actionDispatcher.DescribeIntent(frame);
        bool ok;
        string pill;
        string? error;
        try
        {
            (ok, pill, error) = _actionDispatcher.ExecuteFrame(frame);
        }
        catch (Exception ex)
        {
            _log("ERROR", $"bridge: action '{intent}' threw: {ex.Message}");
            (ok, pill, error) = (false, "failed", null);
        }

        long executedAt = Stopwatch.GetTimestamp();
        double executeMs = Stopwatch.GetElapsedTime(receivedAt, executedAt).TotalMilliseconds;
        AppMetrics.ActionExecuteMs.Record(executeMs);
        (ok ? AppMetrics.ActionsExecutedOk : AppMetrics.ActionsFailed).Add(1);

        (int? volumePercent, bool muted) = ok
            ? _actionDispatcher.DescribeVolumeResult(frame)
            : (null, false);
        if (volumePercent is int commandedVolume)
        {
            _volumePublisher.RecordCommandedState(new VolumeState(commandedVolume, muted));
        }

        if (_config.Current.OverlayEnabled)
        {
            string overlayIntent =
                frame is SetVolumeFrame && volumePercent is not null ? "Volume" : intent;
            _overlaySink?.Invoke(new OverlayContent($"Google Home → {overlayIntent}", ok ? pill : "failed", !ok)
            {
                VolumePercent = volumePercent,
                Muted = muted,
            });
        }

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
            // The session stopped mid-send.
        }

        long ackedAt = Stopwatch.GetTimestamp();
        _log("DEBUG", string.Create(
            CultureInfo.InvariantCulture,
            $"IPC timing: {WireName(frame)} id={frame.Id} execute={executeMs:0.0}ms ack={Stopwatch.GetElapsedTime(executedAt, ackedAt).TotalMilliseconds:0.0}ms total={Stopwatch.GetElapsedTime(receivedAt, ackedAt).TotalMilliseconds:0.0}ms"));
    }

    private void OnStateChanged(object? sender, BridgeState state) =>
        StateChanged?.Invoke(this, state);

    private void OnPairingReceived(object? sender, PairingFrame frame) =>
        PairingReceived?.Invoke(this, frame);

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

}
