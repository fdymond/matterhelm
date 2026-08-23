using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using MatterHelm.Ui;

namespace MatterHelm;

/// <summary>
/// Tray icon color/state (BLUEPRINT §2.4): gray = bridge disabled, amber =
/// running but not yet commissioned, green = paired and connected, red =
/// sidecar crashed/restarting.
/// </summary>
public enum BridgeState
{
    /// <summary>Bridge disabled by the user (gray).</summary>
    Disabled,

    /// <summary>Sidecar running, not yet commissioned by Google Home (amber).</summary>
    Running,

    /// <summary>Paired and connected (green).</summary>
    Connected,

    /// <summary>Sidecar crashed and is restarting (red).</summary>
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

    private bool _applyingConfigChange;
    private bool _disposed;
    private PairingWindow? _pairingWindow;
    private SettingsWindow? _settingsWindow;
    private (string QrPayload, string ManualCode)? _lastPairingInfo;
    private BridgeState _state = BridgeState.Disabled;

    /// <summary>Creates the tray icon and menu, and loads (or creates) <see cref="Config"/>.</summary>
    /// <param name="config">Injectable for tests/demos; defaults to the real <c>%APPDATA%</c> config.</param>
    public TrayContext(Config? config = null)
    {
        Config = config ?? new Config();

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

        _enableBridgeItem = new ToolStripMenuItem("Enable bridge")
        {
            CheckOnClick = true,
            Checked = Config.Current.BridgeEnabled,
        };
        _enableBridgeItem.CheckedChanged += OnEnableBridgeCheckedChanged;

        // BLUEPRINT §2.4: amber's parenthetical is "shows 'Pair…' menu item" —
        // read as "this is when it's actionable", not "only time it exists":
        // the item stays visible always (so users can find it) and is enabled
        // whenever the bridge is running or a pairing code has been cached
        // (see UpdateMenuForState for why Connected must count).
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

        var aboutItem = new ToolStripMenuItem("About");
        aboutItem.Click += OnAboutClicked;

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

        Config.Changed += OnConfigChanged;
        UpdateMenuForState();

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

    /// <summary>The settings window's "Preview" button was clicked; the payload is the STAGED overlay position (S9-1 — preview where the overlay would land after Save). <c>Program</c> shows a sample pop-up there (the HUD lives there, not here).</summary>
    public event EventHandler<OverlayPosition>? OverlayPreviewRequested;

    /// <summary>"Exit" was clicked, before teardown; subscribers should synchronously stop anything they own (e.g. the sidecar).</summary>
    public event EventHandler? ExitRequested;

    /// <summary>The loaded config; also the source of "Reload config"/<see cref="Config.Changed"/> for other components to subscribe to.</summary>
    public Config Config { get; }

    /// <summary>Current tray state.</summary>
    public BridgeState State => _state;

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
            _lastPairingInfo = (qrPayload, manualCode);
            UpdateMenuForState();
            if (_pairingWindow is { IsDisposed: false })
            {
                _pairingWindow.SetPairingInfo(qrPayload, manualCode);
            }
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

    private void ApplyState(BridgeState state)
    {
        _state = state;
        _notifyIcon.Icon = _stateIcons[state];
        UpdateMenuForState();
    }

    private void UpdateMenuForState()
    {
        // "Enable bridge" is left purely user-driven here (not resynced from
        // SetState) so an external state update never fights an in-flight
        // user click; only the "actionable now" hint is state-derived.
        // Pair… must stay enabled while Connected (green): green currently
        // means "sidecar link up", NOT "commissioned" (BridgeHost.DeriveState)
        // — the pairing code arrives over that link, so gating on Running
        // alone would disable the item exactly when the code exists (S2-R
        // finding 1). Enabled whenever the bridge runs or a code is cached.
        _pairItem.Enabled =
            _lastPairingInfo is not null || _state is BridgeState.Running or BridgeState.Connected;
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
        PairRequested?.Invoke(this, EventArgs.Empty);
        ShowOrFocusPairingWindow();
    }

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
                + "  •  Stop the bridge (the sidecar disconnects immediately)" + Environment.NewLine
                + $"  •  Permanently delete the Matter pairing data under {Path.Combine(AppPaths.Root, "matter")}"
                + Environment.NewLine
                + "  •  Make every MatterHelm device show as offline in Google Home until you remove them there"
                + Environment.NewLine
                + "  •  Require re-pairing (a new QR code) afterward" + Environment.NewLine + Environment.NewLine
                + "Your config and logs are kept.",
            "Factory reset bridge",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (choice != DialogResult.Yes)
        {
            return;
        }

        Log.Info("Tray: factory reset confirmed; requesting BridgeHost.FactoryReset().");
        FactoryResetRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ShowOrFocusPairingWindow()
    {
        if (_pairingWindow is null || _pairingWindow.IsDisposed)
        {
            _pairingWindow = new PairingWindow();
            (string qrPayload, string manualCode) = _lastPairingInfo ?? ("MT:PENDING", "Not yet available");
            _pairingWindow.SetPairingInfo(qrPayload, manualCode);
        }

        _pairingWindow.Show();
        _pairingWindow.Activate();
    }

    /// <summary>Same single-instance pattern as the pairing window: recreate only when never opened or closed (disposed), else focus. A fresh window per open also means a fresh staged copy of the config.</summary>
    private void ShowOrFocusSettingsWindow()
    {
        if (_settingsWindow is null || _settingsWindow.IsDisposed)
        {
            _settingsWindow = new SettingsWindow(
                new SettingsViewModel(Config),
                overlayPreview: position => OverlayPreviewRequested?.Invoke(this, position),
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

    private void OnExitClicked(object? sender, EventArgs e)
    {
        Log.Info("Exit requested from tray menu; shutting down.");
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
            _disposed = true;

            Config.Changed -= OnConfigChanged;
            _pairingWindow?.Dispose();
            _settingsWindow?.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _contextMenu.Dispose();
            _uiThreadMarshal.Dispose();
            foreach (Icon icon in _stateIcons.Values)
            {
                icon.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
