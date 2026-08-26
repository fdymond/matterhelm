using QRCoder;

namespace MatterHelm.Ui;

/// <summary>What the pairing window is currently telling the user (S10-7).</summary>
public enum PairingStage
{
    /// <summary>Bridge is off or still starting — there is no code to scan yet.</summary>
    Starting,

    /// <summary>A code is on screen and the hub has not commissioned us yet.</summary>
    ReadyToScan,

    /// <summary>Commissioned: this PC is already in Google Home.</summary>
    Paired,

    /// <summary>The bridge is faulted and cannot currently be discovered.</summary>
    DiscoveryError,
}

/// <summary>
/// "Pair with Google Home…" window: a normal, activatable, focusable
/// <see cref="Form"/> — deliberately NOT the click-through/non-activating
/// overlay recipe used by <see cref="OverlayHud"/>, since the user must be
/// able to interact with (and copy from) this window. Shows the Matter
/// commissioning QR code rendered locally from the <c>qrPayload</c> string
/// (ADR-003 item 6: QRCoder's <see cref="PngByteQRCode"/> renderer — no
/// System.Drawing-coupled QRCode-Core renderer, no cloud QR service) plus the
/// manual pairing code as large, selectable text.
///
/// <para><b>S10-7 onboarding pass.</b> The window is a wizard step, not a
/// picture: it carries numbered instructions naming the real Home-app path,
/// and a live status strip driven by <see cref="SetStage"/> so the user can
/// see the outcome instead of guessing. Four stages:
/// <see cref="PairingStage.Starting"/> (no code yet — QR hidden rather than
/// showing a meaningless placeholder), <see cref="PairingStage.ReadyToScan"/>,
/// <see cref="PairingStage.Paired"/>, which hides the code entirely, and
/// <see cref="PairingStage.DiscoveryError"/>, which replaces a misleading
/// QR code with an actionable failure.</para>
///
/// <para>Layout is DPI-safe (S4-5): a single auto-sized
/// <see cref="TableLayoutPanel"/> column with logical-unit sizes converted via
/// <see cref="Control.LogicalToDeviceUnits(int)"/> and the window sized from
/// its content — never a fixed <c>ClientSize</c>, which clipped the caption
/// and instructions at 200 % scaling (WinForms auto-scaling is a no-op for
/// runtime-built forms on net10, see SettingsWindow's DPI note).</para>
/// </summary>
public sealed class PairingWindow : Form
{
    private const int QrDisplaySizeLogical = 300;
    private static readonly TimeSpan PairedAutoCloseDelay = TimeSpan.FromSeconds(4);

    private readonly PictureBox _qrBox;
    private readonly Label _codeCaption;
    private readonly TextBox _codeBox;
    private readonly Label _heading;
    private readonly Label _steps;
    private readonly Label _status;
    private readonly Label _matterIdentity;
    private readonly LinkLabel _consoleHint;
    private readonly Label _installIdentityHint;
    private readonly TableLayoutPanel _layout;

    private readonly Color _statusOkColor;
    private readonly Color _statusWaitingColor;
    private readonly Action<Action, TimeSpan>? _autoCloseScheduler;
    private readonly System.Windows.Forms.Timer _autoCloseTimer;
    private bool _stageInitialized;
    private bool _autoCloseScheduled;
    private PairingStage _currentStage;

    /// <summary>Builds the (initially empty) window chrome; call <see cref="SetPairingInfo"/> to populate it.</summary>
    /// <param name="vendorId">The active Matter vendor identifier.</param>
    /// <param name="productId">The active Matter product identifier.</param>
    /// <param name="autoCloseScheduler">Optional deterministic timing seam for focused UI tests; production uses a WinForms timer.</param>
    public PairingWindow(
        int vendorId = 0xFFF1,
        int productId = 0x8000,
        Action<Action, TimeSpan>? autoCloseScheduler = null)
    {
        _autoCloseScheduler = autoCloseScheduler;
        Text = "Pair with Google Home";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        // System colors, not literals: dark mode (ADR-005) swaps SystemColors
        // process-wide and hard-coded white turned this window white-on-white.
        BackColor = SystemColors.Window;

        _statusOkColor = Application.IsDarkModeEnabled
            ? Color.FromArgb(87, 196, 116)
            : Color.FromArgb(28, 128, 60);
        _statusWaitingColor = SystemColors.GrayText;

        _layout = new TableLayoutPanel
        {
            // Not docked: the panel auto-sizes to its rows and the form (also
            // auto-sized) wraps it — Dock.Fill made the form under-measure the
            // wrapped instruction label's height.
            Location = new Point(0, 0),
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = SP(30, 24, 30, 22),
        };
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // Stage-aware (SetStage rewrites it): "Add this PC…" is a promise the
        // Paired stage has already kept, and leaving it there read as if the
        // pairing had not taken.
        _heading = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 13.5f, FontStyle.Regular),
            Margin = SP(0, 0, 0, 8),
        };

        // The steps name the ACTUAL Home-app path. Until S10-7 this said
        // "choose Works with Google", which is the cloud account-linking
        // branch — following it, a Matter device can never be added.
        _steps = new Label
        {
            Text = "1.  Open the Google Home app on your phone.\n"
                + "2.  Tap  +  →  Add device  →  Matter-enabled device.\n"
                + "3.  Scan the code below, or enter the digits instead.\n"
                + "4.  Tap past the \"not certified\" notice, then pick a room.",
            AutoSize = true,
            MaximumSize = new Size(S(QrDisplaySizeLogical + 40), 0),
            Font = new Font("Segoe UI", 9.5f),
            Margin = SP(0, 0, 0, 14),
        };

        _qrBox = new PictureBox
        {
            Size = new Size(S(QrDisplaySizeLogical), S(QrDisplaySizeLogical)),
            // CenterImage (not Zoom): SetPairingInfo renders the QR at an exact
            // integer number of device pixels per module, so showing it 1:1
            // keeps it crisp at any DPI — Zoom would resample and blur it.
            SizeMode = PictureBoxSizeMode.CenterImage,
            // Deliberately literal white in BOTH themes: a QR code needs a
            // light quiet zone for phone cameras to lock on — never theme it.
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = SP(0, 0, 0, 0),
            Anchor = AnchorStyles.None,
        };

        _codeCaption = new Label
        {
            Text = "Or enter this code manually",
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = SystemColors.GrayText,
            Margin = SP(0, 14, 0, 0),
        };

        // A read-only TextBox (not a Label) so the code is selectable/copyable —
        // the story requires "large, selectable text" for the manual fallback.
        _codeBox = new TextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Window,
            TextAlign = HorizontalAlignment.Center,
            // 16pt keeps the longest expected manual codes (13 chars incl.
            // dashes) fully in view even though focusing a TextBox via
            // keyboard/programmatic Activate() (rather than a mouse click)
            // selects all its text and can auto-scroll to the caret — at 20pt
            // that scroll clipped the start of the code off-screen.
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            Width = S(QrDisplaySizeLogical),
            Cursor = Cursors.IBeam,
            Margin = SP(0, 2, 0, 0),
        };

        // Live outcome, so the user never has to guess whether it worked.
        _status = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(S(QrDisplaySizeLogical + 40), 0),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Margin = SP(0, 16, 0, 0),
        };

        _matterIdentity = new Label
        {
            Text = $"Active Matter identity:  VID {MatterIds.Format(vendorId)} · PID {MatterIds.Format(productId)}",
            AutoSize = true,
            MaximumSize = new Size(S(QrDisplaySizeLogical + 40), 0),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Margin = SP(0, 16, 0, 0),
        };

        // Google rejects an unregistered test identity before connecting and
        // now often reports only a generic timeout. Keep the exact live pair
        // and the controller-cache recovery step beside the code being used.
        const string developerConsoleName = "Google Home Developer Console";
        _consoleHint = new LinkLabel
        {
            Text = $"These IDs must EXACTLY match a Matter integration in your {developerConsoleName} project. "
                + "Reboot the Nest hub after any Console change.",
            AutoSize = true,
            MaximumSize = new Size(S(QrDisplaySizeLogical + 40), 0),
            Font = new Font("Segoe UI", 8.5f),
            LinkArea = new LinkArea(0, 0),
            Margin = SP(0, 4, 0, 0),
        };
        _consoleHint.Links.Clear();
        _consoleHint.Links.Add(_consoleHint.Text.IndexOf(developerConsoleName, StringComparison.Ordinal), developerConsoleName.Length, "https://console.home.google.com/");
        _consoleHint.LinkClicked += (_, e) => OpenLink(e.Link?.LinkData as string);

        _installIdentityHint = new Label
        {
            Text = "This PC's Matter identity is minted from a unique per-install seed. "
                + "Each computer needs its own; never copy config.json or the seed between PCs.",
            AutoSize = true,
            MaximumSize = new Size(S(QrDisplaySizeLogical + 40), 0),
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = SystemColors.GrayText,
            Margin = SP(0, 8, 0, 0),
        };

        _layout.Controls.Add(_heading);
        _layout.Controls.Add(_steps);
        _layout.Controls.Add(_qrBox);
        _layout.Controls.Add(_codeCaption);
        _layout.Controls.Add(_codeBox);
        _layout.Controls.Add(_status);
        _layout.Controls.Add(_matterIdentity);
        _layout.Controls.Add(_consoleHint);
        _layout.Controls.Add(_installIdentityHint);
        Controls.Add(_layout);

        _autoCloseTimer = new System.Windows.Forms.Timer
        {
            Interval = checked((int)PairedAutoCloseDelay.TotalMilliseconds),
        };
        _autoCloseTimer.Tick += (_, _) => CompleteScheduledAutoClose();

        SetStage(PairingStage.Starting);
    }

    /// <summary>The QR image's client-area bounds, exposed only for demo/E2E objective verification (see <c>Program.RunPairingWindowDemo</c>).</summary>
    public Rectangle QrImageBounds => RectangleToClient(_qrBox.RectangleToScreen(_qrBox.ClientRectangle));

    /// <summary>Whether the scannable code panel is showing, exposed for demo/E2E verification.</summary>
    public bool QrVisible => _qrBox.Visible;

    /// <summary>The status strip's current text, exposed for demo/E2E verification.</summary>
    public string StatusText => _status.Text;

    /// <summary>The stage currently rendered by the window, exposed for focused lifecycle tests.</summary>
    public PairingStage CurrentStage => _currentStage;

    /// <summary>Whether a successful live pairing has scheduled this open window to close.</summary>
    public bool AutoCloseScheduled => _autoCloseScheduled;

    /// <summary>The active VID/PID caption, exposed for focused UI specification tests.</summary>
    public string MatterIdentityText => _matterIdentity.Text;

    /// <summary>The Developer Console matching and hub-reboot guidance, exposed for focused UI specification tests.</summary>
    public string DeveloperConsoleHintText => _consoleHint.Text;

    /// <summary>The per-install seed warning, exposed for focused UI specification tests.</summary>
    public string InstallIdentityHintText => _installIdentityHint.Text;

    /// <summary>Whether the Matter identity helper area applies to the current stage.</summary>
    public bool IdentityHelpVisible => _currentStage == PairingStage.ReadyToScan;

    /// <summary>
    /// Renders <paramref name="qrPayload"/> (the Matter <c>MT:…</c> onboarding
    /// payload) as a QR image and shows <paramref name="manualCode"/> as
    /// selectable text. Safe to call repeatedly on the same open window.
    /// </summary>
    /// <remarks>
    /// Holding a code IS <see cref="PairingStage.ReadyToScan"/> — the bridge only
    /// emits one while it is uncommissioned and listening — so this advances the
    /// stage itself. Without that, a code arriving at an already-open window
    /// (the common case: the user opens it first, the sidecar reports a moment
    /// later) rendered the QR inside a still-hidden panel, under a "Starting
    /// the bridge…" status.
    /// </remarks>
    public void SetPairingInfo(string qrPayload, string manualCode)
    {
        Image? previousImage = _qrBox.Image;
        _qrBox.Image = RenderQrImage(qrPayload);
        previousImage?.Dispose();
        _codeBox.Text = manualCode;
        SetStage(PairingStage.ReadyToScan);
    }

    /// <summary>
    /// Switches the window between its four stages (S10-7 plus advertisement failure). Safe to call
    /// repeatedly; the tray context calls it on every bridge-state change so
    /// an open window follows along live.
    /// </summary>
    public void SetStage(PairingStage stage)
    {
        SetStageCore(stage, showAutoCloseHint: false);
    }

    /// <summary>
    /// Shows the successful-pairing stage immediately, then closes this window
    /// after a short grace period. The user may close it sooner.
    /// </summary>
    public void ShowPairedAndAutoClose()
    {
        SetStageCore(PairingStage.Paired, showAutoCloseHint: true);
        ScheduleAutoClose();
    }

    private void SetStageCore(PairingStage stage, bool showAutoCloseHint)
    {
        if (stage != PairingStage.Paired)
        {
            CancelAutoClose();
        }

        bool stageChanged = !_stageInitialized || _currentStage != stage;
        Screen? currentScreen = stageChanged && Visible
            ? Screen.FromRectangle(Bounds)
            : null;

        SuspendLayout();
        _layout.SuspendLayout();

        _currentStage = stage;
        _stageInitialized = true;
        bool showCode = stage == PairingStage.ReadyToScan;
        _steps.Visible = showCode;
        _qrBox.Visible = showCode;
        _codeCaption.Visible = showCode;
        _codeBox.Visible = showCode;
        _matterIdentity.Visible = showCode;
        _consoleHint.Visible = showCode;
        _installIdentityHint.Visible = showCode;

        _heading.Text = stage == PairingStage.Paired
            ? "This PC is in Google Home"
            : "Add this PC to Google Home";

        (_status.Text, _status.ForeColor) = stage switch
        {
            PairingStage.Paired => (
                "Paired — nothing more to do here.\n"
                    + (showAutoCloseHint ? "This window closes itself in about 4 seconds; you can close it now.\n" : "")
                    + "To pair it again, or to a different home, use "
                    + "\"Factory reset bridge…\" in the tray menu first.",
                _statusOkColor),
            PairingStage.ReadyToScan => (
                "Waiting for the Google Home app… this window updates by itself.",
                _statusWaitingColor),
            PairingStage.DiscoveryError => (
                "Bridge is not available for discovery. Restart it and check the log for the specific cause.",
                Color.Firebrick),
            _ => (
                "Starting the bridge… the code appears here in a few seconds.\n"
                    + "If it doesn't, tick \"Enable bridge\" in the tray menu.",
                _statusWaitingColor),
        };

        // Resolve the content's new preferred size before painting, then move
        // and resize together. The screen was captured before the resize so a
        // large stage cannot accidentally select an adjacent monitor.
        _layout.ResumeLayout(true);
        Size preferredSize = GetPreferredSize(Size.Empty);
        if (currentScreen is not null)
        {
            Rectangle workingArea = currentScreen.WorkingArea;
            int left = workingArea.Left + ((workingArea.Width - preferredSize.Width) / 2);
            int top = workingArea.Top + ((workingArea.Height - preferredSize.Height) / 2);
            SetBounds(left, top, preferredSize.Width, preferredSize.Height, BoundsSpecified.All);
        }

        ResumeLayout(true);
    }

    private void ScheduleAutoClose()
    {
        CancelAutoClose();
        _autoCloseScheduled = true;
        if (_autoCloseScheduler is not null)
        {
            _autoCloseScheduler(CompleteScheduledAutoClose, PairedAutoCloseDelay);
            return;
        }

        _autoCloseTimer.Start();
    }

    private void CancelAutoClose()
    {
        _autoCloseScheduled = false;
        _autoCloseTimer.Stop();
    }

    private void CompleteScheduledAutoClose()
    {
        if (!_autoCloseScheduled || _currentStage != PairingStage.Paired)
        {
            return;
        }

        CancelAutoClose();
        Close();
    }

    private static void OpenLink(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Pairing window: could not open '{url}': {ex.Message}");
        }
    }

    /// <summary>
    /// Renders a QR payload string to a GDI+ <see cref="Bitmap"/> via QRCoder's
    /// <see cref="PngByteQRCode"/> (no System.Drawing coupling in the renderer
    /// itself), at the largest integer device-pixels-per-module that fits the
    /// QR box — integer modules shown 1:1 stay crisp at every DPI.
    /// </summary>
    private Bitmap RenderQrImage(string payload)
    {
        using QRCodeGenerator generator = new();
        using QRCodeData data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        PngByteQRCode pngRenderer = new(data);

        // Probe at 1 px/module to learn the rendered module count (incl. the
        // quiet zone) without assuming QRCoder's matrix layout.
        int moduleCount;
        using (MemoryStream probeStream = new(pngRenderer.GetGraphic(1)))
        using (Image probe = Image.FromStream(probeStream))
        {
            moduleCount = probe.Width;
        }

        int pixelsPerModule = Math.Max(1, _qrBox.ClientSize.Width / moduleCount);
        byte[] png = pngRenderer.GetGraphic(pixelsPerModule);

        using MemoryStream stream = new(png);
        using Image decoded = Image.FromStream(stream);

        // Clone off the MemoryStream-backed image before the stream (and the
        // `using` chain above) is disposed — Image.FromStream's result can
        // keep a lazy reference to its source stream.
        return new Bitmap(decoded);
    }

    /// <summary>Logical (96-dpi) pixels → device pixels; see SettingsWindow's DPI note.</summary>
    private int S(int logical) => LogicalToDeviceUnits(logical);

    /// <summary>Logical (96-dpi) padding → device padding.</summary>
    private Padding SP(int left, int top, int right, int bottom) => new(S(left), S(top), S(right), S(bottom));

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoCloseTimer.Dispose();
            _qrBox.Image?.Dispose();
        }

        base.Dispose(disposing);
    }
}
