using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace HtpcMatterBridge.Ui;

/// <summary>
/// One overlay flash's content. <see cref="VolumePercent"/> non-null (and not
/// an error) switches the result pill to a horizontal volume bar showing the
/// resulting level (S4-5); <see cref="Muted"/> dims that bar and replaces the
/// percent label with "muted". Everything else renders the classic text pill.
/// </summary>
public sealed record OverlayContent(string Primary, string Pill, bool IsError)
{
    /// <summary>Resulting volume level 0–100 to render as a fill bar, or null for the plain text pill.</summary>
    public int? VolumePercent { get; init; }

    /// <summary>True renders the volume bar dimmed with a "muted" label. Only meaningful when <see cref="VolumePercent"/> is set.</summary>
    public bool Muted { get; init; }
}

/// <summary>
/// Persistent, click-through, non-activating flash overlay (BLUEPRINT §2.4,
/// ADR-003 item 5). <see cref="Show(OverlayContent)"/> updates the same window
/// in place: primary line = incoming command ("Google Home → volume 40 %"),
/// pill = executed action/failure — or, for volume actions, a percentage fill
/// bar. Content snaps to full alpha, holds ~2.5 s, then fades ~300 ms. All
/// geometry and fonts scale with the window's startup DPI (S4-5) — the HUD is
/// re-created only with the app, so a mid-session DPI change is deliberately
/// not handled (note: startup DPI is fine per story; the window never moves
/// off the primary monitor). Thread-safe: callers may invoke
/// <see cref="Show(OverlayContent)"/> from any thread — it marshals to the
/// HUD's own UI thread.
/// </summary>
public sealed class OverlayHud : IDisposable
{
    private readonly HudWindow _window;

    /// <summary>Creates and eagerly shows (inactive, alpha 0) the HUD window. Must be called on a UI thread.</summary>
    public OverlayHud()
    {
        _window = new HudWindow();
        _window.Show();
    }

    /// <summary>
    /// Enables or disables the overlay pop-ups feature (bridge for the future
    /// tray "Overlay pop-ups" toggle). When <c>false</c>, <see cref="Show(OverlayContent)"/>
    /// is a no-op. This is unrelated to the underlying window's OS-level
    /// visibility, which stays alive (at alpha 0) for the HUD's whole lifetime
    /// to avoid Show/Hide flicker.
    /// </summary>
    public bool Visible { get; set; } = true;

    /// <summary>Native window handle, exposed only for demo/E2E objective verification (see <see cref="OverlayHudDemo"/>).</summary>
    public IntPtr WindowHandle => _window.Handle;

    /// <summary>Current on-screen rectangle, exposed only for demo/E2E objective verification.</summary>
    public Rectangle Bounds => _window.Bounds;

    /// <summary>Canvas-relative bounds of the volume bar's track, exposed only for demo/E2E objective verification (pixel sampling).</summary>
    public Rectangle VolumeTrackBounds => _window.VolumeTrackBounds;

    /// <summary>Copies the HUD's current canvas bitmap, exposed only for demo/E2E objective verification. Call on the HUD's UI thread after a <see cref="Show(OverlayContent)"/>.</summary>
    public Bitmap CaptureCanvas() => _window.CaptureCanvas();

    /// <summary>Text-pill convenience overload of <see cref="Show(OverlayContent)"/>.</summary>
    public void Show(string primary, string pill, bool isError) => Show(new OverlayContent(primary, pill, isError));

    /// <summary>
    /// Displays (or updates in place) the primary command line and result
    /// pill/volume bar. Resets the hold timer and snaps to full alpha even if
    /// a fade was already in progress, so rapid-fire calls never flicker.
    /// </summary>
    public void Show(OverlayContent content)
    {
        if (!Visible)
        {
            return;
        }

        if (_window.InvokeRequired)
        {
            _window.BeginInvoke(new Action(() => _window.ShowContent(content)));
            return;
        }

        _window.ShowContent(content);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _window.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The actual layered top-level window; kept private so callers only ever see the <see cref="OverlayHud"/> surface.</summary>
    private sealed class HudWindow : Form
    {
        // Logical (96-dpi) design units; every use goes through S()/SF() so the
        // canvas renders at the window's startup DPI (S4-5 — at 200 % the old
        // fixed 460×104 bitmap appeared half-size).
        private const int CanvasWidthLogical = 460;
        private const int CanvasHeightLogical = 104;
        private const float ShadowMarginLogical = 10f;
        private const float PanelRadiusLogical = 14f;
        private const int BottomMarginLogical = 48;
        private const float PillRowFromBottomLogical = 38f;
        private const float PillRowHeightLogical = 24f;
        private const float ContentInsetLogical = 18f;
        private const float VolumeTrackWidthLogical = 150f;
        private const float VolumeTrackHeightLogical = 10f;
        private const float PrimaryFontPxLogical = 15.33f; // 11.5 pt at 96 dpi
        private const float PillFontPxLogical = 12.67f; // 9.5 pt at 96 dpi

        private const int HoldMilliseconds = 2500;
        private const int FadeMilliseconds = 300;
        private const int FadeTimerIntervalMs = 15;

        private const int WmMouseActivate = 0x0021;
        private const int MaNoActivate = 3;

        private readonly float _scale;
        private readonly int _canvasWidth;
        private readonly int _canvasHeight;
        private readonly System.Windows.Forms.Timer _holdTimer;
        private readonly System.Windows.Forms.Timer _fadeTimer;
        private readonly Bitmap _canvas;
        private readonly IntPtr _memDc;
        private readonly IntPtr _dibSection;
        private readonly IntPtr _oldDibSelection;

        private OverlayContent? _last;
        private long _fadeStartTicks;

        internal HudWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;

            // Startup DPI of the primary monitor (the HUD's home); see the
            // OverlayHud class doc for why a live DPI change is not handled.
            _scale = DeviceDpi / 96f;
            _canvasWidth = S(CanvasWidthLogical);
            _canvasHeight = S(CanvasHeightLogical);
            ClientSize = new Size(_canvasWidth, _canvasHeight);

            Rectangle working = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            Location = new Point(
                working.Left + ((working.Width - _canvasWidth) / 2),
                working.Bottom - _canvasHeight - S(BottomMarginLogical));

            (_memDc, _dibSection, _oldDibSelection, _canvas) = CreateLayeredCanvas(_canvasWidth, _canvasHeight);

            _holdTimer = new System.Windows.Forms.Timer { Interval = HoldMilliseconds };
            _holdTimer.Tick += OnHoldElapsed;
            _fadeTimer = new System.Windows.Forms.Timer { Interval = FadeTimerIntervalMs };
            _fadeTimer.Tick += OnFadeTick;
        }

        // ADR-003 item 5: the exact ex-style combination that makes the window
        // click-through and immune to activation.
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WsExLayered | NativeMethods.WsExTransparent |
                    NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow;
                return cp;
            }
        }

        // Paired with WS_EX_NOACTIVATE: this is what lets Show() present the
        // window without WinForms calling SW_SHOW (which would activate it).
        protected override bool ShowWithoutActivation => true;

        internal Rectangle VolumeTrackBounds => Rectangle.Round(VolumeTrackRect());

        internal Bitmap CaptureCanvas() => new(_canvas);

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmMouseActivate)
            {
                // Belt-and-braces per ADR: WS_EX_TRANSPARENT already keeps hit-testing
                // off this window, but refuse activation explicitly too.
                m.Result = (IntPtr)MaNoActivate;
                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Intentional no-op: all pixels are pushed via UpdateLayeredWindow;
            // normal WM_PAINT painting would fight the layered surface.
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Applied via raw SetWindowPos (SWP_NOACTIVATE), not the WinForms
            // `TopMost` property: that property's own internal SetWindowPos call
            // omits SWP_NOACTIVATE, which activates the window as a z-order side
            // effect — silently defeating WS_EX_NOACTIVATE/ShowWithoutActivation.
            NativeMethods.SetWindowPos(
                Handle, NativeMethods.HwndTopmost, 0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);

            // Start fully transparent; the first Show() snaps to opaque.
            PushToScreen(alpha: 0);
        }

        internal void ShowContent(OverlayContent content)
        {
            if (content != _last)
            {
                _last = content;
                Render(content);
            }

            // Rapid-fire calls must never flicker: cancel any in-flight fade and
            // snap straight back to full alpha instead of hiding/showing.
            _fadeTimer.Stop();
            PushToScreen(alpha: 255);

            _holdTimer.Stop();
            _holdTimer.Start();
        }

        private void OnHoldElapsed(object? sender, EventArgs e)
        {
            _holdTimer.Stop();
            _fadeStartTicks = Environment.TickCount64;
            _fadeTimer.Start();
        }

        private void OnFadeTick(object? sender, EventArgs e)
        {
            long elapsed = Environment.TickCount64 - _fadeStartTicks;
            if (elapsed >= FadeMilliseconds)
            {
                _fadeTimer.Stop();
                PushToScreen(alpha: 0);
                return;
            }

            // Re-push the same bitmap at a lower constant alpha; never re-render
            // per tick — only content changes trigger a GDI+ redraw.
            double remaining = 1.0 - (elapsed / (double)FadeMilliseconds);
            PushToScreen(alpha: (byte)Math.Clamp(remaining * 255.0, 0, 255));
        }

        /// <summary>Logical (96-dpi) design units → device pixels (rounded).</summary>
        private int S(int logical) => (int)Math.Round(logical * _scale);

        /// <summary>Logical (96-dpi) design units → device pixels (exact).</summary>
        private float SF(float logical) => logical * _scale;

        private RectangleF PanelRect() => new(
            SF(ShadowMarginLogical),
            SF(ShadowMarginLogical),
            _canvasWidth - (SF(ShadowMarginLogical) * 2),
            _canvasHeight - (SF(ShadowMarginLogical) * 2));

        /// <summary>The pill/bar row: bottom strip of the panel where the result renders.</summary>
        private RectangleF PillRowRect()
        {
            RectangleF panel = PanelRect();
            return new RectangleF(
                panel.X + SF(ContentInsetLogical),
                panel.Bottom - SF(PillRowFromBottomLogical),
                panel.Width - (SF(ContentInsetLogical) * 2),
                SF(PillRowHeightLogical));
        }

        /// <summary>The volume bar's track, vertically centered in the pill row (single source for Render and the demo's pixel sampling).</summary>
        private RectangleF VolumeTrackRect()
        {
            RectangleF row = PillRowRect();
            return new RectangleF(
                row.X,
                row.Y + ((row.Height - SF(VolumeTrackHeightLogical)) / 2f),
                SF(VolumeTrackWidthLogical),
                SF(VolumeTrackHeightLogical));
        }

        private void Render(OverlayContent content)
        {
            using Graphics g = Graphics.FromImage(_canvas);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            RectangleF panelRect = PanelRect();

            // Cheap blur substitute: stacked translucent silhouettes give soft
            // edges without a real Gaussian blur pass.
            for (int i = 4; i >= 1; i--)
            {
                float expand = i * SF(2f);
                using GraphicsPath shadowPath = RoundedRect(
                    RectangleF.Inflate(panelRect, expand, expand), SF(PanelRadiusLogical) + expand);
                using var shadowBrush = new SolidBrush(Color.FromArgb(12, 0, 0, 0));
                g.FillPath(shadowBrush, shadowPath);
            }

            using GraphicsPath panelPath = RoundedRect(panelRect, SF(PanelRadiusLogical));
            using var panelBrush = new SolidBrush(Color.FromArgb(235, 26, 26, 30));
            g.FillPath(panelBrush, panelPath);

            // Pixel-unit fonts: point units would rescale with the process's
            // DPI context too (Graphics.FromImage inherits the desktop DPI in
            // a DPI-aware process), double-applying _scale. Explicit pixels
            // keep the canvas render deterministic at any DPI.
            using var primaryFont = new Font("Segoe UI", SF(PrimaryFontPxLogical), FontStyle.Regular, GraphicsUnit.Pixel);
            using var primaryBrush = new SolidBrush(Color.White);
            using var primaryFormat = new StringFormat
            {
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            var primaryRect = new RectangleF(
                panelRect.X + SF(ContentInsetLogical),
                panelRect.Y + SF(14f),
                panelRect.Width - (SF(ContentInsetLogical) * 2),
                SF(26f));
            g.DrawString(content.Primary, primaryFont, primaryBrush, primaryRect, primaryFormat);

            using var pillFont = new Font("Segoe UI", SF(PillFontPxLogical), FontStyle.Bold, GraphicsUnit.Pixel);
            if (content is { VolumePercent: int volumePercent, IsError: false })
            {
                RenderVolumeBar(g, pillFont, Math.Clamp(volumePercent, 0, 100), content.Muted);
            }
            else
            {
                RenderTextPill(g, pillFont, content.Pill, content.IsError);
            }
        }

        private void RenderTextPill(Graphics g, Font pillFont, string pill, bool isError)
        {
            RectangleF row = PillRowRect();
            Color pillColor = isError ? Color.FromArgb(230, 196, 60, 58) : Color.FromArgb(230, 55, 158, 96);
            SizeF pillTextSize = g.MeasureString(pill, pillFont);
            float pillWidth = Math.Min(row.Width, pillTextSize.Width + SF(28f));
            var pillRect = new RectangleF(row.X, row.Y, pillWidth, row.Height);
            using GraphicsPath pillPath = RoundedRect(pillRect, row.Height / 2f);
            using var pillBrush = new SolidBrush(pillColor);
            g.FillPath(pillBrush, pillPath);

            using var pillTextBrush = new SolidBrush(Color.White);
            using var pillFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString(pill, pillFont, pillTextBrush, pillRect, pillFormat);
        }

        /// <summary>
        /// S4-5 volume bar: a rounded track (dim white over the dark panel)
        /// with an accent fill whose width is proportional to
        /// <paramref name="percent"/>, and the "NN %" label beside it — HUD
        /// palette: fill = the success-pill green, muted = dimmed gray fill
        /// with a "muted" label (the level stays visible so unmute expectations
        /// are clear).
        /// </summary>
        private void RenderVolumeBar(Graphics g, Font labelFont, int percent, bool muted)
        {
            RectangleF row = PillRowRect();
            RectangleF track = VolumeTrackRect();

            using GraphicsPath trackPath = RoundedRect(track, track.Height / 2f);
            using var trackBrush = new SolidBrush(Color.FromArgb(45, 255, 255, 255));
            g.FillPath(trackBrush, trackPath);

            float fillWidth = track.Width * (percent / 100f);
            if (fillWidth >= 1f)
            {
                var fill = new RectangleF(track.X, track.Y, fillWidth, track.Height);
                Color fillColor = muted
                    ? Color.FromArgb(200, 128, 128, 136) // dimmed: level kept, accent dropped
                    : Color.FromArgb(230, 55, 158, 96); // the success-pill green
                using GraphicsPath fillPath = RoundedRect(fill, Math.Min(track.Height / 2f, fillWidth / 2f));
                using var fillBrush = new SolidBrush(fillColor);
                g.FillPath(fillBrush, fillPath);
            }

            string label = muted ? "muted" : $"{percent} %";
            using var labelBrush = new SolidBrush(muted ? Color.FromArgb(255, 176, 176, 184) : Color.White);
            using var labelFormat = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            var labelRect = new RectangleF(
                track.Right + SF(10f), row.Y, row.Right - track.Right - SF(10f), row.Height);
            g.DrawString(label, labelFont, labelBrush, labelRect, labelFormat);
        }

        private void PushToScreen(byte alpha)
        {
            if (!IsHandleCreated)
            {
                return;
            }

            var size = new NativeMethods.Size32(_canvasWidth, _canvasHeight);
            var srcPoint = new NativeMethods.Point32(0, 0);
            var dstPoint = new NativeMethods.Point32(Location.X, Location.Y);
            var blend = new NativeMethods.BlendFunction
            {
                BlendOp = NativeMethods.AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = alpha,
                AlphaFormat = NativeMethods.AcSrcAlpha,
            };

            NativeMethods.UpdateLayeredWindow(
                Handle, IntPtr.Zero, ref dstPoint, ref size, _memDc, ref srcPoint, 0, ref blend, NativeMethods.UlwAlpha);
        }

        private static (IntPtr memDc, IntPtr dib, IntPtr oldSelection, Bitmap canvas) CreateLayeredCanvas(int width, int height)
        {
            IntPtr memDc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);

            var header = new NativeMethods.BitmapInfoHeader
            {
                biSize = Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                biWidth = width,
                biHeight = -height, // negative = top-down DIB, matching GDI+ scanline order
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BiRgb,
            };

            IntPtr dib = NativeMethods.CreateDIBSection(memDc, ref header, NativeMethods.DibRgbColors, out IntPtr bits, IntPtr.Zero, 0);
            IntPtr oldSelection = NativeMethods.SelectObject(memDc, dib);

            // The Bitmap wraps the DIB section's own memory directly (scan0) so
            // GDI+ draws land straight in the surface UpdateLayeredWindow reads.
            var canvas = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
            return (memDc, dib, oldSelection, canvas);
        }

        private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
        {
            float d = radius * 2f;
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _holdTimer.Dispose();
                _fadeTimer.Dispose();
                _canvas.Dispose();

                // Deselect before deleting, standard GDI teardown order.
                NativeMethods.SelectObject(_memDc, _oldDibSelection);
                NativeMethods.DeleteObject(_dibSection);
                NativeMethods.DeleteDC(_memDc);
            }

            base.Dispose(disposing);
        }
    }
}
