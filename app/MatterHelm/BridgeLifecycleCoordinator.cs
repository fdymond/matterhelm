using System.Net;
using MatterHelm.Actions;
using MatterHelm.Diagnostics;
using MatterHelm.Infrastructure;
using MatterHelm.Sidecar;
using MatterHelm.Ui;

namespace MatterHelm;

/// <summary>
/// Serializes bridge lifecycle transitions and owns each server/supervisor
/// session while the host remains the protocol-action façade.
/// </summary>
internal sealed class BridgeLifecycleCoordinator : IDisposable
{
    private const int FaultedRestartThreshold = 2;

    private readonly Config _config;
    private readonly IActionExecutor _executor;
    private readonly SidecarSpec _sidecarSpec;
    private readonly Action<OverlayContent>? _overlaySink;
    private readonly SupervisorOptions? _supervisorOptions;
    private readonly Action<string, string> _log;
    private readonly string _storageDir;
    private readonly VolumeStatePublisher _volumePublisher;
    private readonly BridgeActionDispatcher _actionDispatcher;
    private readonly MatterStorageResetter _storageResetter;
    private readonly Action<IpcServer> _startServer;
    private readonly Lock _gate = new();
    private readonly Lock _lifecycleGate = new();
    private readonly Lock _notifyGate = new();
    private readonly SerialActionQueue _lifecycleQueue;

    private IpcServer? _server;
    private SidecarSupervisor? _supervisor;
    private bool _running;
    private bool _clientAuthenticated;
    private bool _listenerOwnedElsewhere;
    private MatterStatusFrame? _matterStatus;
    private int _restartsSinceAuth;
    private BridgeState _state = BridgeState.Disabled;
    private BridgeState _notifiedState = BridgeState.Disabled;
    private bool _disposed;

    internal BridgeLifecycleCoordinator(
        Config config,
        IActionExecutor executor,
        SidecarSpec sidecarSpec,
        Action<OverlayContent>? overlaySink,
        SupervisorOptions? supervisorOptions,
        Action<string, string> log,
        string storageDir,
        VolumeStatePublisher volumePublisher,
        BridgeActionDispatcher actionDispatcher,
        MatterStorageResetter? storageResetter = null,
        Action<IpcServer>? startServer = null)
    {
        _config = config;
        _executor = executor;
        _sidecarSpec = sidecarSpec;
        _overlaySink = overlaySink;
        _supervisorOptions = supervisorOptions;
        _log = log;
        _storageDir = storageDir;
        _volumePublisher = volumePublisher;
        _actionDispatcher = actionDispatcher;
        _storageResetter = storageResetter ?? new MatterStorageResetter(log);
        _startServer = startServer ?? (server => server.Start());
        _lifecycleQueue = new SerialActionQueue(
            ex => _log("ERROR", $"bridge: lifecycle operation failed: {ex.Message}"));
        _config.Changed += OnConfigChanged;
        _executor.ReconcileMouseMoves(EnabledMouseMoveKeys(config.Current));
    }

    internal event EventHandler<BridgeState>? StateChanged;

    internal event EventHandler<PairingFrame>? PairingReceived;

    internal event Action<IpcServer, ActionFrame>? ActionReceived;

    internal BridgeState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    internal bool IsCurrent(IpcServer server)
    {
        lock (_gate)
        {
            return ReferenceEquals(server, _server);
        }
    }

    internal void SetEnabled(bool enabled)
    {
        lock (_lifecycleGate)
        {
            SetEnabledCore(enabled);
        }
    }

    internal Task QueueSetEnabledAsync(bool enabled) =>
        _lifecycleQueue.EnqueueAsync(() => SetEnabled(enabled));

    internal Task QueueFactoryResetAsync(Action<FactoryResetResult> completed) =>
        _lifecycleQueue.EnqueueAsync(() => completed(FactoryReset()));

    internal FactoryResetResult FactoryReset()
    {
        lock (_lifecycleGate)
        {
            return FactoryResetCore();
        }
    }

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
            if (server is not null)
            {
                _volumePublisher.PublishSnapshot(server);
            }
        }
        else
        {
            _ = _executor.ReleaseDisplayKeepAwake();
        }

        RecomputeState();
    }

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

    public void Dispose()
    {
        _lifecycleQueue.Complete();
        lock (_lifecycleGate)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
            }

            SetEnabledCore(false);
            lock (_gate)
            {
                _disposed = true;
            }
        }

        _config.Changed -= OnConfigChanged;
    }

    internal static BridgeState DeriveState(
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

    internal static bool IsAddressInUse(HttpListenerException exception) =>
        exception.NativeErrorCode is 32 or 48 or 98 or 183 or 10048;

    private void SetEnabledCore(bool enabled)
    {
        IpcServer? stoppingServer = null;
        SidecarSupervisor? stoppingSupervisor = null;
        bool releaseDisplayKeepAwake = false;
        bool stopMacroSession = false;
        StartResult startResult = StartResult.Started;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (enabled == _running)
            {
                releaseDisplayKeepAwake = !enabled;
                stopMacroSession = !enabled;
                if (!enabled)
                {
                    _listenerOwnedElsewhere = false;
                }
            }
            else if (enabled)
            {
                _actionDispatcher.BeginMacroSession();
                startResult = StartLocked();
                if (startResult != StartResult.Started)
                {
                    stopMacroSession = true;
                }
            }
            else
            {
                releaseDisplayKeepAwake = true;
                stopMacroSession = true;
                stoppingSupervisor = _supervisor;
                stoppingServer = _server;
                _supervisor = null;
                _server = null;
                _volumePublisher.SetSender((IpcServer?)null);
                _running = false;
                _clientAuthenticated = false;
                _listenerOwnedElsewhere = false;
                _restartsSinceAuth = 0;
            }
        }

        Task[] macroTasks = stopMacroSession ? _actionDispatcher.CancelMacroSession() : [];
        stoppingSupervisor?.Stop();
        stoppingServer?.Dispose();
        _actionDispatcher.DrainMacroSession(macroTasks);

        if (releaseDisplayKeepAwake)
        {
            _ = _executor.ReleaseDisplayKeepAwake();
            _executor.ClearScreensaverFocusCapture();
        }

        if (startResult == StartResult.Failed && _config.Current.BridgeEnabled)
        {
            _config.Current.BridgeEnabled = false;
            _ = _config.Save();
            _log("WARN", "bridge: start failed; persisted it disabled so the tray setting and host state remain consistent.");
        }

        RecomputeState();
    }

    private FactoryResetResult FactoryResetCore()
    {
        bool restoreEnabled = _config.Current.BridgeEnabled;
        SetEnabledCore(false);

        MatterStorageResetResult storageReset = _storageResetter.Reset(_storageDir);
        if (!storageReset.Completed)
        {
            string? error = storageReset.Error;
            string failure = $"could not stage Matter storage: {error ?? "unknown error"}";
            _log(
                "WARN",
                $"bridge: factory reset could not stage Matter storage at '{_storageDir}' ({error}); "
                    + "the live directory was not changed — the previous enabled state is being restored; retry once whatever "
                    + "holds the folder open (e.g. an antivirus scan or a slow-to-exit sidecar) has released it.");
            if (_config.Current.OverlayEnabled)
            {
                _overlaySink?.Invoke(new OverlayContent("Factory reset failed", failure, IsError: true));
            }

            SetEnabledCore(restoreEnabled);
            if (restoreEnabled && State == BridgeState.Disabled)
            {
                _config.Current.BridgeEnabled = false;
                _ = _config.Save();
                _log("WARN", "bridge: factory-reset recovery could not restart the bridge; persisted it disabled so the tray and host remain consistent.");
            }

            return new FactoryResetResult(false, failure);
        }

        _config.Current.BridgeEnabled = true;
        _config.Save();
        SetEnabledCore(true);

        _log(
            "INFO",
            $"bridge: factory reset complete — live Matter storage at '{_storageDir}' removed; "
                + "bridge restarted and uncommissioned, a fresh pairing code follows in a few seconds.");
        if (_config.Current.OverlayEnabled)
        {
            _overlaySink?.Invoke(new OverlayContent(
                "Factory reset complete", "restarting — a new pairing code is coming", IsError: false));
        }

        return new FactoryResetResult(true, null);
    }

    private StartResult StartLocked()
    {
        BridgeConfig sessionConfig = _config.Current;
        int port = sessionConfig.IpcPort;
        _storageResetter.SweepStaleResidue(_storageDir);
        _listenerOwnedElsewhere = false;
        _actionDispatcher.SetSessionPowerOffAction(sessionConfig.PowerOffAction);
        var supervisor = new SidecarSupervisor(
            _sidecarSpec,
            port,
            _storageDir,
            sessionConfig.LogLevel,
            _supervisorOptions,
            _log,
            SidecarEnvironment.BuildExtraEnv(sessionConfig));
        var server = new IpcServer(port, supervisor.IpcToken, log: _log);
        server.ActionReceived += OnActionReceived;
        server.PairingReceived += OnPairingReceived;
        server.MatterStatusReceived += OnMatterStatusReceived;
        server.ClientChanged += OnClientChanged;
        supervisor.RestartScheduled += OnRestartScheduled;

        try
        {
            _startServer(server);
        }
        catch (HttpListenerException ex)
        {
            bool ownedElsewhere = IsAddressInUse(ex);
            _listenerOwnedElsewhere = ownedElsewhere;
            string reason = ownedElsewhere ? "the IPC port is owned elsewhere" : ex.Message;
            _log("ERROR", $"bridge: cannot listen on port {port} ({reason}); bridge stays disabled.");
            server.Dispose();
            supervisor.Dispose();
            return ownedElsewhere ? StartResult.OwnedElsewhere : StartResult.Failed;
        }

        supervisor.Start();
        _server = server;
        _volumePublisher.SetSender(server);
        _supervisor = supervisor;
        _running = true;
        _clientAuthenticated = false;
        _matterStatus = null;
        _restartsSinceAuth = 0;
        _log("INFO", $"bridge: started (port {port}).");
        return StartResult.Started;
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        _executor.ReconcileMouseMoves(UnchangedMouseMoveKeys(e.OldConfig, e.NewConfig));
        bool leftDisplayMode = IsDisplayPowerAction(e.OldConfig.PowerOffAction)
            && !IsDisplayPowerAction(e.NewConfig.PowerOffAction);
        if (leftDisplayMode)
        {
            _ = _executor.ReleaseDisplayKeepAwake();
            _log("INFO", "bridge: power-off behavior left a display mode; released any display keep-awake hold.");
        }

        if (!e.NewConfig.BridgeEnabled || !BridgeRestartPolicy.RequiresRestart(e.OldConfig, e.NewConfig))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || (!_running && !_listenerOwnedElsewhere))
            {
                return;
            }
        }

        _ = _lifecycleQueue.EnqueueAsync(RestartAfterConfigChange);
    }

    private void RestartAfterConfigChange()
    {
        lock (_lifecycleGate)
        {
            lock (_gate)
            {
                if (_disposed || (!_running && !_listenerOwnedElsewhere))
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
    }

    private void OnActionReceived(object? sender, ActionFrame frame)
    {
        if (sender is IpcServer server && IsCurrent(server))
        {
            ActionReceived?.Invoke(server, frame);
        }
    }

    private void OnPairingReceived(object? sender, PairingFrame frame)
    {
        if (sender is not IpcServer server || !IsCurrent(server))
        {
            return;
        }

        _log("INFO", "bridge: pairing payload received from sidecar.");
        if (_config.Current.OverlayEnabled)
        {
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
        if (sender is IpcServer server && IsCurrent(server))
        {
            HandleAuthenticatedClientChanged(connected, server);
        }
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

    private void RecomputeState()
    {
        lock (_gate)
        {
            BridgeState derived = _listenerOwnedElsewhere
                ? BridgeState.Faulted
                : DeriveState(_running, _clientAuthenticated, _restartsSinceAuth, _matterStatus);
            if (derived == _state)
            {
                return;
            }

            _state = derived;
        }

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

    private static bool IsDisplayPowerAction(PowerOffAction action) =>
        action is PowerOffAction.DisplaysOff or PowerOffAction.PauseAndDisplaysOff;

    private static HashSet<string> EnabledMouseMoveKeys(BridgeConfig config) =>
        config.Commands.Custom
            .Where(command => command.Enabled && command.Action is MouseMoveActionConfig)
            .Select(command => command.Key)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> UnchangedMouseMoveKeys(BridgeConfig before, BridgeConfig after)
    {
        Dictionary<string, MouseMoveActionConfig> oldActions = before.Commands.Custom
            .Where(command => command.Enabled && command.Action is MouseMoveActionConfig)
            .ToDictionary(
                command => command.Key,
                command => (MouseMoveActionConfig)command.Action,
                StringComparer.Ordinal);
        return after.Commands.Custom
            .Where(command => command.Enabled && command.Action is MouseMoveActionConfig)
            .Where(command => oldActions.TryGetValue(command.Key, out MouseMoveActionConfig? oldAction)
                && command.Action is MouseMoveActionConfig newAction
                && oldAction.Target == newAction.Target
                && oldAction.X == newAction.X
                && oldAction.Y == newAction.Y)
            .Select(command => command.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Classifies listener startup so an occupied port remains enabled but other failures persist disabled.</summary>
    private enum StartResult
    {
        Started,
        OwnedElsewhere,
        Failed,
    }
}
