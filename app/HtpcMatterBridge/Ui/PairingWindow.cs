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
/// without the user having to reopen the window.
/// </summary>
public sealed class PairingWindow : Form
{
    private const int QrPixelsPerModule = 8;
    private const int QrDisplaySize = 300;

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
        ClientSize = new Size(360, 470);
        // System colors, not literals: dark mode (ADR-005) swaps SystemColors
        // process-wide and hard-coded white turned this window white-on-white.
        BackColor = SystemColors.Window;

        _qrBox = new PictureBox
        {
            Location = new Point(30, 24),
            Size = new Size(QrDisplaySize, QrDisplaySize),
            SizeMode = PictureBoxSizeMode.Zoom,
            // Deliberately literal white in BOTH themes: a QR code needs a
            // light quiet zone for phone cameras to lock on — never theme it.
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
        };

        var codeCaption = new Label
        {
            Text = "Manual pairing code",
            Location = new Point(30, 336),
            Size = new Size(QrDisplaySize, 18),
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = SystemColors.GrayText,
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
            Location = new Point(30, 356),
            Size = new Size(QrDisplaySize, 40),
            Cursor = Cursors.IBeam,
        };

        var instructions = new Label
        {
            Text = "Open the Google Home app, tap Add device, choose \"Works with Google\", "
                + "then scan this code or enter the manual code above.",
            Location = new Point(30, 404),
            Size = new Size(QrDisplaySize, 56),
            Font = new Font("Segoe UI", 9f),
        };

        Controls.Add(_qrBox);
        Controls.Add(codeCaption);
        Controls.Add(_codeBox);
        Controls.Add(instructions);
    }

    /// <summary>The QR image's client-area bounds, exposed only for demo/E2E objective verification (see <c>Program.RunPairingWindowDemo</c>).</summary>
    public Rectangle QrImageBounds => _qrBox.Bounds;

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

    /// <summary>Renders a QR payload string to a GDI+ <see cref="Image"/> via QRCoder's <see cref="PngByteQRCode"/> (no System.Drawing coupling in the renderer itself).</summary>
    private static Image RenderQrImage(string payload)
    {
        using QRCodeGenerator generator = new();
        using QRCodeData data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        PngByteQRCode pngRenderer = new(data);
        byte[] png = pngRenderer.GetGraphic(QrPixelsPerModule);

        using MemoryStream stream = new(png);
        using Image decoded = Image.FromStream(stream);

        // Clone off the MemoryStream-backed image before the stream (and the
        // `using` chain above) is disposed — Image.FromStream's result can
        // keep a lazy reference to its source stream.
        return new Bitmap(decoded);
    }

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
