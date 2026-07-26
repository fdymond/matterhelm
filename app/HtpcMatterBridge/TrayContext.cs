using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using HtpcMatterBridge.Ui;

namespace HtpcMatterBridge;

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
/// with no sidecar/IPC/executor logic here — S2-5 wires this to the
/// supervisor, IPC server, and action executor.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly Control _uiThreadMarshal;
    private readonly NotifyIcon _notifyIcon;
    private readonly Dictionary<BridgeState, Icon> _stateIcons;
    private readonly ToolStripMenuItem _enableBridgeItem;
    private readonly ToolStripMenuItem _pairItem;
    private readonly ToolStripMenuItem _overlayItem;

    private bool _applyingConfigChange;
    private PairingWindow? _pairingWindow;
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

        _stateIcons = new Dictionary<BridgeState, Icon>
        {
            [BridgeState.Disabled] = CreateSolidCircleIcon(Color.Gray),
            [BridgeState.Running] = CreateSolidCircleIcon(Color.FromArgb(255, 179, 0)),
            [BridgeState.Connected] = CreateSolidCircleIcon(Color.FromArgb(46, 160, 67)),
            [BridgeState.Faulted] = CreateSolidCircleIcon(Color.FromArgb(213, 48, 48)),
        };

        _enableBridgeItem = new ToolStripMenuItem("Enable bridge") { CheckOnClick = true, Checked = true };
        _enableBridgeItem.CheckedChanged += OnEnableBridgeCheckedChanged;

        // BLUEPRINT §2.4: amber's parenthetical is "shows 'Pair…' menu item" —
        // read as "this is when it's actionable", not "only time it exists":
        // the item stays visible always (so users can find it) but is only
        // enabled while running-and-uncommissioned.
        _pairItem = new ToolStripMenuItem("Pair with Google Home…");
        _pairItem.Click += OnPairClicked;

        _overlayItem = new ToolStripMenuItem("Overlay pop-ups")
        {
            CheckOnClick = true,
            Checked = Config.Current.OverlayEnabled,
        };
        _overlayItem.CheckedChanged += OnOverlayCheckedChanged;

        var deviceNamesItem = new ToolStripMenuItem("Device names…");
        deviceNamesItem.Click += (_, _) => OpenConfigFile();

        var openConfigItem = new ToolStripMenuItem("Open config");
        openConfigItem.Click += (_, _) => OpenConfigFolder();

        var reloadConfigItem = new ToolStripMenuItem("Reload config");
        reloadConfigItem.Click += OnReloadConfigClicked;

        var aboutItem = new ToolStripMenuItem("About");
        aboutItem.Click += OnAboutClicked;

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += OnExitClicked;

        var menu = new ContextMenuStrip();
        menu.Items.Add(_enableBridgeItem);
        menu.Items.Add(_pairItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_overlayItem);
        menu.Items.Add(deviceNamesItem);
        menu.Items.Add(openConfigItem);
        menu.Items.Add(reloadConfigItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(aboutItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _stateIcons[_state],
            Text = "HTPC Matter Bridge",
            ContextMenuStrip = menu,
            Visible = true,
        };

        Config.Changed += OnConfigChanged;
        UpdateMenuForState();

        Log.Info("TrayContext started; icon visible.");
    }

    /// <summary>"Enable bridge" was toggled; payload is the new checked state. S2-5 starts/stops the sidecar supervisor.</summary>
    public event EventHandler<bool>? EnableBridgeChanged;

    /// <summary>"Pair with Google Home…" was clicked. S2-5 may use this to ensure the sidecar/commissioning window is active.</summary>
    public event EventHandler? PairRequested;

    /// <summary>"Overlay pop-ups" was toggled (already persisted to <see cref="Config"/> by the time this fires); S2-5 flips the live <c>OverlayHud</c>.</summary>
    public event EventHandler<bool>? OverlayEnabledChanged;

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
        _pairItem.Enabled = _state == BridgeState.Running;
    }

    private void OnEnableBridgeCheckedChanged(object? sender, EventArgs e)
    {
        bool enabled = _enableBridgeItem.Checked;
        Log.Info($"Tray: 'Enable bridge' set to {enabled} (S2-5 wires the actual sidecar start/stop).");
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

    private void OnOverlayCheckedChanged(object? sender, EventArgs e)
    {
        if (_applyingConfigChange)
        {
            return; // Programmatic sync from Config.Changed, not a user click.
        }

        bool enabled = _overlayItem.Checked;
        Config.Current.OverlayEnabled = enabled;
        Config.Save();
        Log.Info($"Tray: overlay pop-ups set to {enabled} and saved to config.");
        OverlayEnabledChanged?.Invoke(this, enabled);
    }

    private void OnReloadConfigClicked(object? sender, EventArgs e)
    {
        Log.Info("Tray: 'Reload config' clicked.");
        Config.Reload();
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        // Keep the checkbox honest if the file was hand-edited before Reload.
        _applyingConfigChange = true;
        try
        {
            _overlayItem.Checked = e.NewConfig.OverlayEnabled;
        }
        finally
        {
            _applyingConfigChange = false;
        }
    }

    private void OpenConfigFile()
    {
        OpenWithShell(Config.DefaultPath, "config.json");
    }

    private void OpenConfigFolder()
    {
        string? dir = Path.GetDirectoryName(Config.DefaultPath);
        if (dir is not null)
        {
            OpenWithShell(dir, "config folder");
        }
    }

    private static void OpenWithShell(string path, string what)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Tray: failed to open {what} ('{path}'): {ex.Message}");
        }
    }

    private static void OnAboutClicked(object? sender, EventArgs e)
    {
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        MessageBox.Show(
            $"HTPC Matter Bridge{Environment.NewLine}Version {version?.ToString() ?? "unknown"}",
            "About",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        Log.Info("Exit requested from tray menu; shutting down.");
        ExitRequested?.Invoke(this, EventArgs.Empty);

        Config.Changed -= OnConfigChanged;
        _pairingWindow?.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _uiThreadMarshal.Dispose();
        foreach (Icon icon in _stateIcons.Values)
        {
            icon.Dispose();
        }

        Application.Exit();
    }

    /// <summary>Draws a filled, outlined circle at 16x16 and returns it as an owned <see cref="Icon"/> (GDI+; no .ico assets).</summary>
    private static Icon CreateSolidCircleIcon(Color color)
    {
        const int size = 16;
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fillBrush = new SolidBrush(color);
            g.FillEllipse(fillBrush, 1, 1, size - 2, size - 2);
            using var outlinePen = new Pen(Color.FromArgb(110, Color.Black), 1f);
            g.DrawEllipse(outlinePen, 1, 1, size - 2, size - 2);
        }

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            // Icon.FromHandle does not own the native HICON; clone into a
            // GDI+-managed Icon so the raw handle can be destroyed immediately
            // instead of leaking for the tray icon's lifetime.
            using Icon borrowed = Icon.FromHandle(hIcon);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
