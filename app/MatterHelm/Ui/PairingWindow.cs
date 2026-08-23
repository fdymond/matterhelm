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
/// see the outcome instead of guessing. Three stages:
/// <see cref="PairingStage.Starting"/> (no code yet — QR hidden rather than
/// showing a meaningless placeholder), <see cref="PairingStage.ReadyToScan"/>,
/// and <see cref="PairingStage.Paired"/>, which hides the code entirely —
/// matter.js cannot mint a fresh code once commissioned (BLUEPRINT §2.1), so
/// leaving a dead QR on screen invites a scan that can only fail.</para>
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

    private readonly PictureBox _qrBox;
    private readonly Label _codeCaption;
    private readonly TextBox _codeBox;
    private readonly Label _heading;
    private readonly Label _steps;
    private readonly Label _status;
    private readonly LinkLabel _consoleHint;

    private readonly Color _statusOkColor;
    private readonly Color _statusWaitingColor;

    /// <summary>Builds the (initially empty) window chrome; call <see cref="SetPairingInfo"/> to populate it.</summary>
    public PairingWindow()
    {
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

        var layout = new TableLayoutPanel
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
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

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

        // The #1 cause of a hard pairing failure is skipping the one-time
        // Developer Console registration, so it is one click away from here.
        _consoleHint = new LinkLabel
        {
            Text = "Pairing rejected as \"not certified\"? Register VID 0xFFF1 / PID 0x8000 "
                + "in your free Google Home Developer Console project.",
            AutoSize = true,
            MaximumSize = new Size(S(QrDisplaySizeLogical + 40), 0),
            Font = new Font("Segoe UI", 8.5f),
            LinkArea = new LinkArea(0, 0),
            Margin = SP(0, 12, 0, 0),
        };
        _consoleHint.Links.Clear();
        _consoleHint.Links.Add(_consoleHint.Text.IndexOf("Google Home Developer Console", StringComparison.Ordinal), 29, "https://console.home.google.com/");
        _consoleHint.LinkClicked += (_, e) => OpenLink(e.Link?.LinkData as string);

        layout.Controls.Add(_heading);
        layout.Controls.Add(_steps);
        layout.Controls.Add(_qrBox);
        layout.Controls.Add(_codeCaption);
        layout.Controls.Add(_codeBox);
        layout.Controls.Add(_status);
        layout.Controls.Add(_consoleHint);
        Controls.Add(layout);

        SetStage(PairingStage.Starting);
    }

    /// <summary>The QR image's client-area bounds, exposed only for demo/E2E objective verification (see <c>Program.RunPairingWindowDemo</c>).</summary>
    public Rectangle QrImageBounds => RectangleToClient(_qrBox.RectangleToScreen(_qrBox.ClientRectangle));

    /// <summary>Whether the scannable code panel is showing, exposed for demo/E2E verification.</summary>
    public bool QrVisible => _qrBox.Visible;

    /// <summary>The status strip's current text, exposed for demo/E2E verification.</summary>
    public string StatusText => _status.Text;

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
    /// Switches the window between its three stages (S10-7). Safe to call
    /// repeatedly; the tray context calls it on every bridge-state change so
    /// an open window follows along live.
    /// </summary>
    public void SetStage(PairingStage stage)
    {
        bool showCode = stage == PairingStage.ReadyToScan;
        _steps.Visible = showCode;
        _qrBox.Visible = showCode;
        _codeCaption.Visible = showCode;
        _codeBox.Visible = showCode;
        _consoleHint.Visible = showCode;

        _heading.Text = stage == PairingStage.Paired
            ? "This PC is in Google Home"
            : "Add this PC to Google Home";

        (_status.Text, _status.ForeColor) = stage switch
        {
            PairingStage.Paired => (
                "Paired — nothing more to do here.\n"
                    + "To pair it again, or to a different home, use "
                    + "\"Factory reset bridge…\" in the tray menu first.",
                _statusOkColor),
            PairingStage.ReadyToScan => (
                "Waiting for the Google Home app… this window updates by itself.",
                _statusWaitingColor),
            _ => (
                "Starting the bridge… the code appears here in a few seconds.\n"
                    + "If it doesn't, tick \"Enable bridge\" in the tray menu.",
                _statusWaitingColor),
        };
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
            _qrBox.Image?.Dispose();
        }

        base.Dispose(disposing);
    }
}
