using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using MatterHelm.Ui;
using MatterHelm.Updates;

namespace MatterHelm;

/// <summary>
/// Tray icon color/state: gray = bridge disabled, amber = starting, blue =
/// running and awaiting commissioning, green = paired and connected, red =
/// sidecar crashed/restarting or its commissionable advertisement is missing.
/// </summary>
public enum BridgeState
{
    /// <summary>Bridge disabled by the user (gray).</summary>
    Disabled,

    /// <summary>Bridge is starting or has not reported Matter lifecycle status yet (amber).</summary>
    Running,

    /// <summary>Sidecar authenticated and Matter reports uncommissioned (blue).</summary>
    AwaitingPairing,

    /// <summary>Paired and connected (green).</summary>
    Connected,

    /// <summary>Sidecar crash loop or commissionable advertisement missing (red).</summary>
    Faulted,
}

/// <summary>
/// Tray icon, context menu, and pairing-window lifecycle (BLUEPRINT §2.4).
/// Deliberately dumb UI: menu actions surface as events (or are handled
/// directly when they are pure UI/config concerns, e.g. "Reload config"),
/// with no sidecar/IPC/executor logic here — <c>Program</c> wires the events
/// to <see cref="BridgeHost"/>, which owns the supervisor, IPC server, and
/// action executor.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly Control _uiThreadMarshal;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly Dictionary<BridgeState, Icon> _stateIcons;
    private readonly ToolStripMenuItem _enableBridgeItem;
    private readonly ToolStripMenuItem _pairItem;
    private readonly ToolStripMenuItem _factoryResetItem;
    private readonly ToolStripMenuItem _overlayItem;
    private readonly ToolStripMenuItem _checkUpdatesItem;
    private readonly UpdateService _updateService;
    private readonly Func<UpdateRelease, string, string, IUpdateInstallationProbe, CancellationToken, Task<bool>> _startUpdateHandoff;
    private readonly Action<string, string> _showError;
    private readonly Action<Action, TimeSpan>? _pairingAutoCloseScheduler;
    private readonly System.Threading.Timer _updateTimer;
    private readonly CancellationTokenSource _updatesCts = new();
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly Lock _updateTasksLock = new();
    private readonly HashSet<Task> _updateTasks = [];

    private bool _applyingConfigChange;
    private bool _exiting;
    private bool _updateHandoffStarted;
    private bool _updateHandoffCanceledByWindow;
    private volatile bool _disposed;
    private PairingWindow? _pairingWindow;
    private SettingsWindow? _settingsWindow;
    private WelcomeWindow? _welcomeWindow;
    private (string QrPayload, string ManualCode)? _lastPairingInfo;
    private readonly System.Windows.Forms.Timer _pairingSettleTimer;
    private bool _pairingSettled;
    private bool _commissioned;
    private BridgeState _state = BridgeState.Disabled;
    private SemanticVersion? _lastNotifiedUpdate;

    /// <summary>Creates the tray icon and menu, and loads (or creates) <see cref="Config"/>.</summary>
    /// <param name="config">Injectable for tests/demos; defaults to the real <c>%APPDATA%</c> config.</param>
    /// <param name="updateService">Injectable updater for tests; defaults to the production GitHub service.</param>
    /// <param name="pairingAutoCloseScheduler">Optional deterministic timing seam for focused pairing-window tests.</param>
    /// <param name="startUpdateHandoff">Optional post-exit helper launcher seam for focused handoff tests.</param>
    /// <param name="showError">Optional user-visible error sink for focused tests.</param>
    /// <param name="commissionedAtStartup">Optional startup fabric-presence seam; production uses the same Matter-storage signal as first-run onboarding.</param>
    public TrayContext(
        Config? config = null,
        UpdateService? updateService = null,
        Action<Action, TimeSpan>? pairingAutoCloseScheduler = null,
        Func<UpdateRelease, string, string, IUpdateInstallationProbe, CancellationToken, Task<bool>>? startUpdateHandoff = null,
        Action<string, string>? showError = null,
        bool? commissionedAtStartup = null)
    {
        bool usingProductionConfig = config is null;
        Config = config ?? new Config();
        if (Config.TakeLastLoadWarning() is { } loadWarning)
        {
            Log.Warn($"Tray: {loadWarning.Replace(Environment.NewLine, "; ", StringComparison.Ordinal)}");
        }

        _commissioned = commissionedAtStartup
            ?? (usingProductionConfig && Directory.Exists(Path.Combine(AppPaths.Root, "matter")));
        _updateService = updateService ?? UpdateService.CreateDefault();
        _pairingAutoCloseScheduler = pairingAutoCloseScheduler;
        _startUpdateHandoff = startUpdateHandoff ?? UpdateHandoff.StartAsync;
        _showError = showError ?? ((title, message) => MessageBox.Show(
            message,
            title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error));

        // A plain, never-shown Control purely as an Invoke/InvokeRequired
        // anchor (same technique OverlayHud's HudWindow uses): forcing its
        // handle now, on the thread that constructs this context, means
        // InvokeRequired is reliable from the very first SetState/
        // SetPairingInfo call — unlike a captured SynchronizationContext,
        // which would be a stale instance once WinForms lazily installs its
        // own WindowsFormsSynchronizationContext on this thread.
        _uiThreadMarshal = new Control();
        _ = _uiThreadMarshal.Handle;

        _stateIcons = Ui.TrayIcons.CreateStateIcons();
        _stateIcons[BridgeState.AwaitingPairing] = CreateAwaitingPairingIcon();

        _enableBridgeItem = new ToolStripMenuItem("Enable bridge")
        {
            CheckOnClick = true,
            Checked = Config.Current.BridgeEnabled,
        };
        _enableBridgeItem.CheckedChanged += OnEnableBridgeCheckedChanged;

        // S10-23: bridge controls are contextual. An uncommissioned install
        // offers one direct Pair action; a commissioned install offers the
        // normal bridge on/off toggle instead.
        _pairItem = new ToolStripMenuItem("Pair with Google Home…");
        _pairItem.Click += OnPairClicked;

        // BLUEPRINT §2.5 names this a menu action, directly under Pair…: the
        // unpair step you'd reach for right before re-pairing. Always enabled
        // — even a never-paired (gray) bridge has a storage dir worth
        // clearing pre-emptively — the confirmation dialog is the safety net.
        _factoryResetItem = new ToolStripMenuItem("Factory reset bridge…");
        _factoryResetItem.Click += (_, _) => ConfirmAndRequestFactoryReset(owner: null);

        _overlayItem = new ToolStripMenuItem("Overlay pop-ups")
        {
            CheckOnClick = true,
            Checked = Config.Current.OverlayEnabled,
        };
        _overlayItem.CheckedChanged += OnOverlayCheckedChanged;

        // S4-3: "Settings…" replaces "Device names…"/"Open config" — both
        // actions live inside the settings window's Advanced page now
        // (ADR-004 §4); "Reload config" stays for parity with the window.
        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => ShowOrFocusSettingsWindow();

        var reloadConfigItem = new ToolStripMenuItem("Reload config");
        reloadConfigItem.Click += OnReloadConfigClicked;

        var setupGuideItem = new ToolStripMenuItem("Setup guide…");
        setupGuideItem.Click += (_, _) => ShowWelcomeWindow();

        var aboutItem = new ToolStripMenuItem("About");
        aboutItem.Click += OnAboutClicked;

        _checkUpdatesItem = new ToolStripMenuItem("Check for updates…");
        _checkUpdatesItem.Click += OnCheckUpdatesClicked;

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += OnExitClicked;

        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add(_enableBridgeItem);
        _contextMenu.Items.Add(_pairItem);
        _contextMenu.Items.Add(_factoryResetItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(_overlayItem);
        _contextMenu.Items.Add(settingsItem);
        _contextMenu.Items.Add(reloadConfigItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(setupGuideItem);
        _contextMenu.Items.Add(_checkUpdatesItem);
        _contextMenu.Items.Add(aboutItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _stateIcons[_state],
            Text = "MatterHelm",
            ContextMenuStrip = _contextMenu,
            Visible = true,
        };

        // Preserve S10-7's short visual settle before showing the commissioned
        // stage; BridgeHost now supplies the actual lifecycle state.
        _pairingSettleTimer = new System.Windows.Forms.Timer { Interval = 4_000 };
        _pairingSettleTimer.Tick += (_, _) =>
        {
            _pairingSettleTimer.Stop();
            _pairingSettled = true;
            RefreshPairingWindowStage();
        };

        Config.Changed += OnConfigChanged;
        UpdateMenuForState();

        // S10-11: never compete with startup/sidecar work. The callback also
        // re-checks config each time, so Reload config applies the flag live.
        _updateTimer = new System.Threading.Timer(
            _ => StartTrackedUpdate(RunBackgroundUpdateCheckAsync),
            state: null,
            dueTime: TimeSpan.FromMinutes(1),
            period: TimeSpan.FromHours(24));

        Log.Info("TrayContext started; icon visible.");
    }

    /// <summary>"Enable bridge" was toggled (already persisted to <see cref="Config"/> by the time this fires); payload is the new checked state. <c>Program</c> starts/stops the <see cref="BridgeHost"/>.</summary>
    public event EventHandler<bool>? EnableBridgeChanged;

    /// <summary>"Pair with Google Home…" was clicked (the cached pairing window is shown either way).</summary>
    public event EventHandler? PairRequested;

    /// <summary>"Factory reset bridge…" was clicked and confirmed (from the tray menu or Settings → Advanced); <c>Program</c> calls <see cref="BridgeHost.FactoryReset"/> on a worker thread.</summary>
    public event EventHandler? FactoryResetRequested;

    /// <summary>"Overlay pop-ups" was toggled (already persisted to <see cref="Config"/> by the time this fires); <c>Program</c> flips the live <c>OverlayHud</c>.</summary>
    public event EventHandler<bool>? OverlayEnabledChanged;

    /// <summary>The settings window's "Preview" button was clicked; the payload is the STAGED overlay position/theme/opacity (S9-1/S9-4). <c>Program</c> shows a sample pop-up with those values (the HUD lives there, not here).</summary>
    public event EventHandler<Ui.OverlayPreviewRequest>? OverlayPreviewRequested;

    /// <summary>"Exit" was clicked, before teardown; subscribers should synchronously stop anything they own (e.g. the sidecar).</summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    /// The welcome window's primary button and the unpaired tray action both
    /// use this path: it enables the bridge through the normal persisted
    /// checkbox flow, then opens the pairing window.
    /// </summary>
    public void StartGuidedPairing()
    {
        if (!_enableBridgeItem.Checked)
        {
            _enableBridgeItem.Checked = true;
        }
        else if (_state == BridgeState.Disabled)
        {
            // Config can still say enabled after a failed/aborted start. Pair
            // is an explicit retry request, so do not leave the user looking
            // at a bridge-off window just because the checkbox was already
            // persisted true.
            Log.Info("Tray: pairing requested while the persisted bridge state is enabled but stopped; retrying start.");
            EnableBridgeChanged?.Invoke(this, true);
        }

        ShowOrFocusPairingWindow();
    }

    /// <summary>Shows the first-run/setup guide window (tray menu, or automatically on a never-paired install).</summary>
    public void ShowWelcomeWindow()
    {
        if (_welcomeWindow is null || _welcomeWindow.IsDisposed)
        {
            _welcomeWindow = new WelcomeWindow(Config.Current.VendorId, Config.Current.ProductId);
            _welcomeWindow.StartPairingRequested += (_, _) => StartGuidedPairing();
        }

        _welcomeWindow.Show();
        _welcomeWindow.Activate();
    }

    /// <summary>The loaded config; also the source of "Reload config"/<see cref="Config.Changed"/> for other components to subscribe to.</summary>
    public Config Config { get; }

    /// <summary>Current tray state.</summary>
    public BridgeState State => _state;

    /// <summary>Current tray tooltip text, exposed for focused state-selection tests.</summary>
    public string ToolTipText => _notifyIcon.Text;

    /// <summary>Whether a pairing window is currently open, exposed for focused lifecycle tests.</summary>
    public bool PairingWindowOpen => _pairingWindow is { IsDisposed: false, Visible: true };

    /// <summary>The open pairing window's current stage, or null when no window is open.</summary>
    public PairingStage? PairingWindowStage => PairingWindowOpen ? _pairingWindow!.CurrentStage : null;

    /// <summary>The open pairing window title, exposed for focused bridge-off rendering tests.</summary>
    public string? PairingWindowTitle => PairingWindowOpen ? _pairingWindow!.Text : null;

    /// <summary>Whether Pair with Google Home is currently actionable.</summary>
    public bool PairMenuEnabled => _pairItem.Enabled;

    /// <summary>Whether the contextual Pair with Google Home action is visible.</summary>
    public bool PairMenuVisible => _pairItem.Available;

    /// <summary>Whether the contextual Enable bridge toggle is visible.</summary>
    public bool EnableBridgeMenuVisible => _enableBridgeItem.Available;

    /// <summary>Whether Factory reset remains available in the current lifecycle state.</summary>
    public bool FactoryResetMenuVisible => _factoryResetItem.Available;

    /// <summary>Whether the tray currently renders the bridge as enabled.</summary>
    public bool BridgeMenuChecked => _enableBridgeItem.Checked;

    /// <summary>Whether the open pairing window is waiting to close after a live commissioning event.</summary>
    public bool PairingWindowAutoCloseScheduled => PairingWindowOpen && _pairingWindow!.AutoCloseScheduled;

    /// <summary>
    /// Sets the tray icon/menu to reflect <paramref name="state"/>. Safe to
    /// call from any thread — marshals to the UI thread that constructed this
    /// context when necessary.
    /// </summary>
    public void SetState(BridgeState state)
    {
        if (_uiThreadMarshal.InvokeRequired)
        {
            _uiThreadMarshal.BeginInvoke(new Action(() => ApplyState(state)));
        }
        else
        {
            ApplyState(state);
        }
    }

    /// <summary>
    /// Called by S2-5 when the sidecar emits (or rotates) the commissioning
    /// payload: updates an already-open <see cref="PairingWindow"/> in place,
    /// and caches the info so the next "Pair with Google Home…" click shows
    /// it immediately.
    /// </summary>
    public void SetPairingInfo(string qrPayload, string manualCode)
    {
        void Apply()
        {
            if (_state == BridgeState.Disabled)
            {
                Log.Warn("Tray: ignored a late pairing payload because the bridge is off.");
                return;
            }

            // Never log the code itself (it is a commissioning secret), but do
            // record that it CHANGED: a stale code on screen and a fresh one in
            // hand look identical in the log otherwise, and that is exactly the
            // failure mode behind a "can't find device" re-pair (S10-8).
            bool replaced = _lastPairingInfo is { } previous && previous.QrPayload != qrPayload;
            _lastPairingInfo = (qrPayload, manualCode);
            _pairingSettleTimer.Stop();
            _pairingSettled = false;
            if (replaced)
            {
                Log.Info("Tray: pairing code replaced by a newer one from the sidecar; any open window now shows the new code.");
            }

            UpdateMenuForState();
            if (_pairingWindow is { IsDisposed: false })
            {
                _pairingWindow.SetPairingInfo(qrPayload, manualCode);
            }

            RefreshPairingWindowStage();
        }

        if (_uiThreadMarshal.InvokeRequired)
        {
            _uiThreadMarshal.BeginInvoke(new Action(Apply));
        }
        else
        {
            Apply();
        }
    }

    /// <summary>
    /// Creates the blue awaiting-pairing variant from the existing runtime
    /// HELM silhouette, preserving its antialiased alpha at the system tray
    /// size without introducing a separate icon asset.
    /// </summary>
    private static Icon CreateAwaitingPairingIcon()
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using Bitmap bitmap = Ui.TrayIcons.Render(BridgeState.Running, size, darkTaskbar: true);
        Color blue = Color.FromArgb(0, 120, 212);
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.A > 0)
                {
                    bitmap.SetPixel(x, y, Color.FromArgb(pixel.A, blue));
                }
            }
        }

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using Icon borrowed = Icon.FromHandle(hIcon);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            _ = Ui.NativeMethods.DestroyIcon(hIcon);
        }
    }

    private void ApplyState(BridgeState state)
    {
        bool commissionedNow = _state != BridgeState.Connected && state == BridgeState.Connected;
        bool autoCloseOpenPairingWindow = commissionedNow && PairingWindowOpen;
        _state = state;
        if (state == BridgeState.Connected)
        {
            _commissioned = true;
        }
        else if (state == BridgeState.AwaitingPairing)
        {
            _commissioned = false;
        }

        _notifyIcon.Icon = _stateIcons[state];

        // S10-7: the tooltip says what the icon colour means, so hovering
        // answers "is it working?" without opening anything. NotifyIcon.Text
        // is capped at 63 chars by Windows - keep these short.
        _notifyIcon.Text = state switch
        {
            BridgeState.Running => "MatterHelm - bridge starting",
            BridgeState.AwaitingPairing => "MatterHelm — running, not paired yet",
            BridgeState.Connected => "MatterHelm - bridge running",
            BridgeState.Faulted => "MatterHelm - bridge problem, see the log",
            _ => "MatterHelm - bridge off",
        };

        // Restart the settle window on every state change: only a link that
        // STAYS up without a code means "already commissioned".
        _pairingSettled = false;
        _pairingSettleTimer.Stop();
        if (state == BridgeState.Disabled)
        {
            // Commissioning data is session-scoped. A disabled bridge is not
            // advertising, so retaining its code would make Pair appear
            // actionable and could present a stale code after the next start.
            _lastPairingInfo = null;
        }
        else if (state == BridgeState.Connected)
        {
            // A commissioning code belongs only to an uncommissioned node.
            // Clear it as soon as Matter reports success so it cannot outrank
            // the Paired stage or be shown on a later open.
            _lastPairingInfo = null;
            if (autoCloseOpenPairingWindow)
            {
                _pairingSettled = true;
            }
            else
            {
                // Preserve the established already-paired opening behavior:
                // a closed window stays closed, and the short settle covers
                // startup ordering before a later manual open shows Paired.
                _pairingSettleTimer.Start();
            }
        }

        UpdateMenuForState();
        if (autoCloseOpenPairingWindow)
        {
            _pairingWindow!.ShowPairedAndAutoClose();
        }
        else
        {
            RefreshPairingWindowStage();
        }
    }

    /// <summary>Pushes the current stage into an open pairing window so it follows bridge state live (S10-7).</summary>
    private void RefreshPairingWindowStage()
    {
        if (_pairingWindow is { IsDisposed: false })
        {
            _pairingWindow.Text = _state == BridgeState.Disabled
                ? "Pair with Google Home — bridge off"
                : "Pair with Google Home";
            _pairingWindow.SetStage(CurrentPairingStage);
        }
    }

    private void UpdateMenuForState()
    {
        // "Enable bridge" is left purely user-driven here (not resynced from
        // SetState) so an external state update never fights an in-flight
        // user click. Visibility follows only the authoritative Matter
        // commissioned report remembered across transient/off states.
        _enableBridgeItem.Available = _commissioned;
        _pairItem.Available = !_commissioned;
        _pairItem.Enabled = !_commissioned;
        _factoryResetItem.Available = true;
    }

    private void OnEnableBridgeCheckedChanged(object? sender, EventArgs e)
    {
        bool enabled = _enableBridgeItem.Checked;
        if (!_applyingConfigChange)
        {
            // A user click persists; a Config.Changed sync must not re-save
            // what the file already says.
            Config.Current.BridgeEnabled = enabled;
            Config.Save();
        }

        Log.Info($"Tray: 'Enable bridge' set to {enabled}.");
        if (!enabled)
        {
            // The one state transition this class can assert on its own
            // authority: "disabled" is exactly what the checkbox unticked
            // means, regardless of what the sidecar/IPC link is doing.
            SetState(BridgeState.Disabled);
        }

        EnableBridgeChanged?.Invoke(this, enabled);
    }

    private void OnPairClicked(object? sender, EventArgs e)
    {
        StartGuidedPairing();
        PairRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void PerformPairMenuClick() => _pairItem.PerformClick();

    /// <summary>
    /// Shows the factory-reset confirmation (BLUEPRINT §2.5 consequences,
    /// spelled out in full since this is destructive and only asked once) and
    /// raises <see cref="FactoryResetRequested"/> on Yes. Shared by the tray
    /// menu item (<paramref name="owner"/> null — no window to parent to) and
    /// the Settings → Advanced button (<paramref name="owner"/> the settings
    /// window, via the callback <see cref="ShowOrFocusSettingsWindow"/> wires
    /// into it).
    /// </summary>
    private void ConfirmAndRequestFactoryReset(IWin32Window? owner)
    {
        DialogResult choice = MessageBox.Show(
            owner,
            "Factory reset the bridge?" + Environment.NewLine + Environment.NewLine
                + "This will:" + Environment.NewLine
                + "  •  Restart the bridge (the sidecar disconnects, then comes back unpaired)" + Environment.NewLine
                + $"  •  Permanently delete the Matter pairing data under {Path.Combine(AppPaths.Root, "matter")}"
                + Environment.NewLine
                + "  •  Make every MatterHelm device show as offline in Google Home until you remove them there"
                + Environment.NewLine
                + "  •  Require re-pairing — a NEW code is issued, and the pairing window opens by itself once it is ready" + Environment.NewLine + Environment.NewLine
                + "Your config and logs are kept.",
            "Factory reset bridge",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (choice != DialogResult.Yes)
        {
            return;
        }

        // Drop the cached pairing code BEFORE the reset runs. It belongs to the
        // fabric about to be deleted, and matter.js mints a new passcode and
        // discriminator on the next start — so a phone that scans it looks for
        // a device that no longer exists and fails with "can't find device".
        // The window falls back to "starting…" until the fresh code lands.
        _lastPairingInfo = null;
        _pairingSettled = false;
        _pairingSettleTimer.Stop();
        UpdateMenuForState();
        RefreshPairingWindowStage();

        Log.Info("Tray: factory reset confirmed; requesting BridgeHost.FactoryReset().");
        FactoryResetRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called by <c>Program</c> once <see cref="BridgeHost.FactoryReset"/> has
    /// returned (S10-8). On success the bridge is already restarting and
    /// uncommissioned, so this syncs the "Enable bridge" tick (the reset turns
    /// the bridge on — see BridgeHost.FactoryReset) and opens the pairing
    /// window, which shows "starting…" and then swaps itself to the fresh code
    /// the moment the sidecar reports it. Safe to call from any thread.
    /// </summary>
    public void OnFactoryResetCompleted(FactoryResetResult result)
    {
        void Apply()
        {
            _applyingConfigChange = true;
            try
            {
                _enableBridgeItem.Checked = Config.Current.BridgeEnabled;
            }
            finally
            {
                _applyingConfigChange = false;
            }

            if (!result.Ok)
            {
                string detail = result.Error ?? "The Matter storage directory could not be cleared.";
                _showError(
                    "Factory reset failed",
                    "MatterHelm could not factory-reset the bridge. The previous enabled state was restored when possible."
                        + Environment.NewLine + Environment.NewLine + detail);
                return;
            }

            _commissioned = false;
            UpdateMenuForState();
            ShowOrFocusPairingWindow();
        }

        if (_uiThreadMarshal.InvokeRequired)
        {
            _uiThreadMarshal.BeginInvoke(new Action(Apply));
        }
        else
        {
            Apply();
        }
    }

    private void ShowOrFocusPairingWindow()
    {
        if (_pairingWindow is null || _pairingWindow.IsDisposed)
        {
            _pairingWindow = new PairingWindow(
                Config.Current.VendorId,
                Config.Current.ProductId,
                _pairingAutoCloseScheduler);
        }

        // S10-7: only render a code when we actually have one - the old
        // "MT:PENDING" placeholder drew a real (meaningless) QR that a phone
        // would happily try to scan.
        if (_lastPairingInfo is { } info)
        {
            _pairingWindow.SetPairingInfo(info.QrPayload, info.ManualCode);
        }

        PairingStage stage = CurrentPairingStage;
        _pairingWindow.Text = _state == BridgeState.Disabled
            ? "Pair with Google Home — bridge off"
            : "Pair with Google Home";
        _pairingWindow.SetStage(stage);
        _pairingWindow.Show();
        _pairingWindow.Activate();
        Log.Info($"Tray: pairing window shown at stage '{stage}'.");
    }

    /// <summary>
    /// What the pairing window should be showing. A red bridge state takes
    /// precedence over a cached code so an unobservable advertisement is
    /// never presented as scannable. The settle fallback remains for older
    /// timing paths where the IPC link authenticates just before status/code.
    /// </summary>
    private PairingStage CurrentPairingStage =>
        _state == BridgeState.Disabled ? PairingStage.Starting
        : _state == BridgeState.Faulted ? PairingStage.DiscoveryError
        : _pairingSettled && _state == BridgeState.Connected ? PairingStage.Paired
        : _lastPairingInfo is not null ? PairingStage.ReadyToScan
        : PairingStage.Starting;

    /// <summary>Same single-instance pattern as the pairing window: recreate only when never opened or closed (disposed), else focus. A fresh window per open also means a fresh staged copy of the config.</summary>
    private void ShowOrFocusSettingsWindow()
    {
        if (_settingsWindow is null || _settingsWindow.IsDisposed)
        {
            _settingsWindow = new SettingsWindow(
                new SettingsViewModel(Config),
                overlayPreview: request => OverlayPreviewRequested?.Invoke(this, request),
                factoryReset: () => ConfirmAndRequestFactoryReset(_settingsWindow));
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnOverlayCheckedChanged(object? sender, EventArgs e)
    {
        bool enabled = _overlayItem.Checked;
        if (!_applyingConfigChange)
        {
            // A user click persists; a Config.Changed sync must not re-save
            // what the file already says.
            Config.Current.OverlayEnabled = enabled;
            Config.Save();
        }

        Log.Info($"Tray: overlay pop-ups set to {enabled}.");
        OverlayEnabledChanged?.Invoke(this, enabled);
    }

    private void OnReloadConfigClicked(object? sender, EventArgs e)
    {
        Log.Info("Tray: 'Reload config' clicked.");
        Config.Reload();
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        bool restartRequired = BridgeRestartPolicy.RequiresRestart(e.OldConfig, e.NewConfig);
        bool recreatePairingWindow = PairingWindowOpen && restartRequired;
        if (restartRequired)
        {
            _lastPairingInfo = null;
            _pairingSettled = false;
            _pairingSettleTimer.Stop();
            UpdateMenuForState();
        }

        // Keep the checkboxes honest if the file was hand-edited before
        // Reload. An actual flip still raises the corresponding *Changed
        // event (CheckedChanged fires), so the live bridge/overlay follow the
        // file — the guard only suppresses the redundant re-save.
        _applyingConfigChange = true;
        try
        {
            _overlayItem.Checked = e.NewConfig.OverlayEnabled;
            _enableBridgeItem.Checked = e.NewConfig.BridgeEnabled;
        }
        finally
        {
            _applyingConfigChange = false;
        }

        if (recreatePairingWindow)
        {
            _pairingWindow!.Dispose();
            _pairingWindow = null;
            ShowOrFocusPairingWindow();
        }
    }

    private static void OnAboutClicked(object? sender, EventArgs e)
    {
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        MessageBox.Show(
            $"MatterHelm{Environment.NewLine}Version {version?.ToString() ?? "unknown"}",
            "About",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void OnCheckUpdatesClicked(object? sender, EventArgs e) =>
        StartTrackedUpdate(RunManualUpdateCheckAsync);

    private async Task RunManualUpdateCheckAsync()
    {
        _checkUpdatesItem.Enabled = false;
        _checkUpdatesItem.Text = "Checking for updates…";
        try
        {
            await _updateGate.WaitAsync(_updatesCts.Token);
            try
            {
                UpdateCheckResult result = await _updateService.CheckForUpdatesAsync(_updatesCts.Token);
                await ShowManualUpdateResultAsync(result);
            }
            finally
            {
                _updateGate.Release();
            }
        }
        catch (OperationCanceledException) when (_updatesCts.IsCancellationRequested)
        {
            // App teardown owns cancellation; no UI is useful while exiting.
        }
        finally
        {
            if (!_disposed)
            {
                _checkUpdatesItem.Text = "Check for updates…";
                _checkUpdatesItem.Enabled = true;
            }
        }
    }

    private async Task ShowManualUpdateResultAsync(UpdateCheckResult result)
    {
        if (result.Status != UpdateCheckStatus.UpdateAvailable || result.Release is null)
        {
            MessageBox.Show(
                result.Message,
                "MatterHelm updates",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        UpdateRelease release = result.Release;
        string mode = release.InstallMode == UpdateInstallMode.Installed ? "installer" : "portable zip";
        DialogResult consent = MessageBox.Show(
            $"MatterHelm {release.Version} is available ({mode})."
                + Environment.NewLine + Environment.NewLine
                + "Download, verify, and install it now? MatterHelm will close while the update is applied.",
            "MatterHelm update available",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2);
        if (consent != DialogResult.Yes)
        {
            Log.Info($"Update {release.Version}: user declined download.");
            return;
        }

        _checkUpdatesItem.Text = "Downloading update…";
        UpdateDownloadResult download = await _updateService.DownloadAndVerifyAsync(
            release,
            userConsented: true,
            _updatesCts.Token);
        if (download.Status != UpdateDownloadStatus.Ready
            || download.PackagePath is null
            || download.ExpectedSha256 is null)
        {
            MessageBox.Show(
                download.Message,
                "MatterHelm update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        bool handedOff = await StartUpdateHandoffAfterClosingWindowsAsync(
            release,
            download.PackagePath,
            download.ExpectedSha256,
            new WindowsUpdateInstallationProbe(),
            _updatesCts.Token);
        if (!handedOff)
        {
            if (_updateHandoffCanceledByWindow)
            {
                Log.Info("Update handoff canceled because an open window declined to close; no helper was started.");
                return;
            }

            MessageBox.Show(
                "The update was verified, but the post-exit update helper could not start. MatterHelm will keep running.",
                "MatterHelm update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        BeginIrreversibleExit("Verified update handed off; shutting down for update.");
    }

    /// <summary>
    /// Resolves every cancelable close before reserving and starting the one
    /// post-exit helper. A failed helper start releases the reservation so a
    /// later user-initiated retry remains possible.
    /// </summary>
    internal async Task<bool> StartUpdateHandoffAfterClosingWindowsAsync(
        UpdateRelease release,
        string packagePath,
        string expectedSha256,
        IUpdateInstallationProbe installationProbe,
        CancellationToken cancellationToken = default)
    {
        _updateHandoffCanceledByWindow = false;
        if (!TryCloseOpenWindows())
        {
            _updateHandoffCanceledByWindow = true;
            return false;
        }

        lock (_updateTasksLock)
        {
            if (_updateHandoffStarted || _exiting || _disposed)
            {
                return false;
            }

            _updateHandoffStarted = true;
        }

        bool handedOff;
        try
        {
            handedOff = await _startUpdateHandoff(
                release,
                packagePath,
                expectedSha256,
                installationProbe,
                cancellationToken);
        }
        catch
        {
            lock (_updateTasksLock)
            {
                _updateHandoffStarted = false;
            }

            throw;
        }

        if (!handedOff)
        {
            lock (_updateTasksLock)
            {
                _updateHandoffStarted = false;
            }
        }

        return handedOff;
    }

    private async Task RunBackgroundUpdateCheckAsync()
    {
        if (_disposed || !Config.Current.UpdateCheckEnabled || !_updateGate.Wait(0))
        {
            return;
        }

        try
        {
            UpdateCheckResult result = await _updateService.CheckForUpdatesAsync(_updatesCts.Token).ConfigureAwait(false);
            if (result.Status == UpdateCheckStatus.UpdateAvailable
                && result.Release is { } release
                && _lastNotifiedUpdate != release.Version)
            {
                _lastNotifiedUpdate = release.Version;
                ShowUpdateAvailableBalloon(release.Version);
            }
        }
        catch (OperationCanceledException) when (_updatesCts.IsCancellationRequested)
        {
            // Normal app teardown.
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private void StartTrackedUpdate(Func<Task> operation)
    {
        Task task;
        lock (_updateTasksLock)
        {
            if (_disposed)
            {
                return;
            }

            task = operation();
            _updateTasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_updateTasksLock)
                {
                    _updateTasks.Remove(completed);
                }

                if (completed.Exception is { } exception)
                {
                    Log.Error($"Update task failed unexpectedly: {exception.GetBaseException().Message}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ShowUpdateAvailableBalloon(SemanticVersion version)
    {
        void Show()
        {
            if (_disposed)
            {
                return;
            }

            _notifyIcon.BalloonTipTitle = "Update available";
            _notifyIcon.BalloonTipText = $"MatterHelm {version} is available — open the tray menu to update.";
            _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(5_000);
        }

        if (_uiThreadMarshal.InvokeRequired)
        {
            _uiThreadMarshal.BeginInvoke(new Action(Show));
        }
        else
        {
            Show();
        }
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        RequestExit("Exit requested from tray menu; shutting down.");
    }

    private void RequestExit(string logMessage)
    {
        lock (_updateTasksLock)
        {
            if (_exiting || _updateHandoffStarted || _disposed)
            {
                return;
            }
        }

        if (!TryCloseOpenWindows())
        {
            Log.Info("Exit canceled by an open window.");
            return;
        }

        BeginIrreversibleExit(logMessage);
    }

    private bool TryCloseOpenWindows()
    {
        if (_settingsWindow is { IsDisposed: false })
        {
            _settingsWindow.Close();
            if (!_settingsWindow.IsDisposed)
            {
                return false;
            }
        }

        _pairingWindow?.Close();
        _welcomeWindow?.Close();
        return true;
    }

    private void BeginIrreversibleExit(string logMessage)
    {
        lock (_updateTasksLock)
        {
            if (_exiting || _disposed)
            {
                return;
            }

            _exiting = true;
        }

        Log.Info(logMessage);
        ExitRequested?.Invoke(this, EventArgs.Empty);

        // Teardown itself now lives in Dispose(bool): Application.Run's
        // message loop disposes this ApplicationContext once Exit() unwinds
        // it, so it happens exactly once regardless of how the loop ends.
        Application.Exit();
    }

    /// <summary>
    /// Idempotent teardown: unsubscribes <see cref="Config.Changed"/>,
    /// disposes the pairing/settings windows, hides then disposes the notify
    /// icon (hidden first so no ghost icon lingers in the tray), the context
    /// menu (which disposes its items), the cached state icons, and the
    /// UI-thread marshal control. Invoked once by <see cref="Application.Run(ApplicationContext)"/>
    /// after the message loop exits (see <see cref="OnExitClicked"/>) — never
    /// call this directly.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            Task[] updateTasks;
            lock (_updateTasksLock)
            {
                _disposed = true;
                updateTasks = [.. _updateTasks];
            }

            _updateTimer.Dispose();
            _updatesCts.Cancel();
            Config.Changed -= OnConfigChanged;
            _pairingWindow?.Dispose();
            _settingsWindow?.Dispose();
            _welcomeWindow?.Dispose();
            _pairingSettleTimer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _contextMenu.Dispose();
            _uiThreadMarshal.Dispose();
            foreach (Icon icon in _stateIcons.Values)
            {
                icon.Dispose();
            }

            Task allUpdates = Task.WhenAll(updateTasks);
            try
            {
                _ = allUpdates.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
                // StartTrackedUpdate observes and logs task failures; teardown still owns cleanup.
            }

            if (allUpdates.IsCompleted)
            {
                DisposeUpdateResources();
            }
            else
            {
                Log.Warn("Update task did not stop within one second; deferring updater disposal until it completes.");
                _ = allUpdates.ContinueWith(
                    _ => DisposeUpdateResources(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        base.Dispose(disposing);
    }

    private void DisposeUpdateResources()
    {
        _updateService.Dispose();
        _updatesCts.Dispose();
    }
}
