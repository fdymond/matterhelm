using QRCoder;

namespace HtpcMatterBridge.Ui;

/// <summary>
/// "Pair with Google Home…" window: a normal, activatable, focusable
/// <see cref="Form"/> — deliberately NOT the click-through/non-activating
/// overlay recipe used by <see cref="OverlayHud"/>, since the user must be
/// able to interact with (and copy from) this window. Shows the Matter
/// commissioning QR code rendered locally from the <c>qrPayload</c> string
/// (ADR-003 item 6: QRCoder's <see cref="PngByteQRCode"/> renderer — no
/// System.Drawing-coupled QRCode-Core renderer, no cloud QR service) plus the
/// manual pairing code as large, selectable text, and a short instruction
/// line. <see cref="SetPairingInfo"/> may be called again on an already-open
/// window so a rotated code (e.g. after a factory reset) updates in place
/// without the user having to reopen the window. Layout is DPI-safe (S4-5): a
/// single auto-sized <see cref="TableLayoutPanel"/> column with logical-unit
/// sizes converted via <see cref="Control.LogicalToDeviceUnits(int)"/> and the
/// window sized from its content — never a fixed <c>ClientSize</c>, which
/// clipped the caption and instructions at 200 % scaling (WinForms
/// auto-scaling is a no-op for runtime-built forms on net10, see
/// SettingsWindow's DPI note).
/// </summary>
public sealed class PairingWindow : Form
{
    private const int QrDisplaySizeLogical = 300;

    private readonly PictureBox _qrBox;
    private readonly TextBox _codeBox;

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

        var layout = new TableLayoutPanel
        {
            // Not docked: the panel auto-sizes to its rows and the form (also
            // auto-sized) wraps it — Dock.Fill made the form under-measure the
            // wrapped instruction label's height.
            Location = new Point(0, 0),
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = SP(30, 24, 30, 20),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

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
        };

        var codeCaption = new Label
        {
            Text = "Manual pairing code",
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

        var instructions = new Label
        {
            Text = "Open the Google Home app, tap Add device, choose \"Works with Google\", "
                + "then scan this code or enter the manual code above.",
            AutoSize = true,
            // Wrap at the QR's width; height then comes from the text itself,
            // so the label can never be cut off by the window edge again.
            MaximumSize = new Size(S(QrDisplaySizeLogical), 0),
            Font = new Font("Segoe UI", 9f),
            Margin = SP(0, 10, 0, 0),
        };

        layout.Controls.Add(_qrBox);
        layout.Controls.Add(codeCaption);
        layout.Controls.Add(_codeBox);
        layout.Controls.Add(instructions);
        Controls.Add(layout);
    }

    /// <summary>The QR image's client-area bounds, exposed only for demo/E2E objective verification (see <c>Program.RunPairingWindowDemo</c>).</summary>
    public Rectangle QrImageBounds => RectangleToClient(_qrBox.RectangleToScreen(_qrBox.ClientRectangle));

    /// <summary>
    /// Renders <paramref name="qrPayload"/> (the Matter <c>MT:…</c> onboarding
    /// payload) as a QR image and shows <paramref name="manualCode"/> as
    /// selectable text. Safe to call repeatedly on the same open window.
    /// </summary>
    public void SetPairingInfo(string qrPayload, string manualCode)
    {
        Image? previousImage = _qrBox.Image;
        _qrBox.Image = RenderQrImage(qrPayload);
        previousImage?.Dispose();
        _codeBox.Text = manualCode;
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
