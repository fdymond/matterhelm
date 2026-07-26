using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace HtpcMatterBridge.Ui;

/// <summary>
/// Persistent, click-through, non-activating flash overlay (BLUEPRINT §2.4,
/// ADR-003 item 5). <see cref="Show"/> updates the same window in place:
/// primary line = incoming command ("Google Home → volume 40 %"), pill =
/// executed action/failure. Content snaps to full alpha, holds ~2.5 s, then
/// fades ~300 ms. Thread-safe: callers may invoke <see cref="Show"/> from any
/// thread — it marshals to the HUD's own UI thread.
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
    /// tray "Overlay pop-ups" toggle). When <c>false</c>, <see cref="Show"/> is
    /// a no-op. This is unrelated to the underlying window's OS-level
    /// visibility, which stays alive (at alpha 0) for the HUD's whole lifetime
    /// to avoid Show/Hide flicker.
    /// </summary>
    public bool Visible { get; set; } = true;

    /// <summary>Native window handle, exposed only for demo/E2E objective verification (see <see cref="OverlayHudDemo"/>).</summary>
    public IntPtr WindowHandle => _window.Handle;

    /// <summary>Current on-screen rectangle, exposed only for demo/E2E objective verification.</summary>
    public Rectangle Bounds => _window.Bounds;

    /// <summary>
    /// Displays (or updates in place) the primary command line and result
    /// pill. Resets the hold timer and snaps to full alpha even if a fade was
    /// already in progress, so rapid-fire calls never flicker.
    /// </summary>
    public void Show(string primary, string pill, bool isError)
    {
        if (!Visible)
        {
            return;
        }

        if (_window.InvokeRequired)
        {
            _window.BeginInvoke(new Action(() => _window.ShowContent(primary, pill, isError)));
            return;
        }

        _window.ShowContent(primary, pill, isError);
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
        private const int CanvasWidth = 460;
        private const int CanvasHeight = 104;
        private const int ShadowMargin = 10;
        private const float PanelRadius = 14f;
        private const int HoldMilliseconds = 2500;
        private const int FadeMilliseconds = 300;
        private const int FadeTimerIntervalMs = 15;
        private const int BottomMargin = 48;

        private const int WmMouseActivate = 0x0021;
        private const int MaNoActivate = 3;

        private readonly System.Windows.Forms.Timer _holdTimer;
        private readonly System.Windows.Forms.Timer _fadeTimer;
        private readonly Bitmap _canvas;
        private readonly IntPtr _memDc;
        private readonly IntPtr _dibSection;
        private readonly IntPtr _oldDibSelection;

        private string? _lastPrimary;
        private string? _lastPill;
        private bool _lastIsError;
        private long _fadeStartTicks;

        internal HudWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            ClientSize = new Size(CanvasWidth, CanvasHeight);

            Rectangle working = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            Location = new Point(
                working.Left + ((working.Width - CanvasWidth) / 2),
                working.Bottom - CanvasHeight - BottomMargin);

            (_memDc, _dibSection, _oldDibSelection, _canvas) = CreateLayeredCanvas(CanvasWidth, CanvasHeight);

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

        internal void ShowContent(string primary, string pill, bool isError)
        {
            bool contentChanged = primary != _lastPrimary || pill != _lastPill || isError != _lastIsError;
            if (contentChanged)
            {
                _lastPrimary = primary;
                _lastPill = pill;
                _lastIsError = isError;
                Render(primary, pill, isError);
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

        private void Render(string primary, string pill, bool isError)
        {
            using Graphics g = Graphics.FromImage(_canvas);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var panelRect = new RectangleF(
                ShadowMargin,
                ShadowMargin,
                CanvasWidth - (ShadowMargin * 2),
                CanvasHeight - (ShadowMargin * 2));

            // Cheap blur substitute: stacked translucent silhouettes give soft
            // edges without a real Gaussian blur pass.
            for (int i = 4; i >= 1; i--)
            {
                float expand = i * 2f;
                using GraphicsPath shadowPath = RoundedRect(RectangleF.Inflate(panelRect, expand, expand), PanelRadius + expand);
                using var shadowBrush = new SolidBrush(Color.FromArgb(12, 0, 0, 0));
                g.FillPath(shadowBrush, shadowPath);
            }

            using GraphicsPath panelPath = RoundedRect(panelRect, PanelRadius);
            using var panelBrush = new SolidBrush(Color.FromArgb(235, 26, 26, 30));
            g.FillPath(panelBrush, panelPath);

            using var primaryFont = new Font("Segoe UI", 11.5f, FontStyle.Regular, GraphicsUnit.Point);
            using var primaryBrush = new SolidBrush(Color.White);
            using var primaryFormat = new StringFormat
            {
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            var primaryRect = new RectangleF(panelRect.X + 18, panelRect.Y + 14, panelRect.Width - 36, 26);
            g.DrawString(primary, primaryFont, primaryBrush, primaryRect, primaryFormat);

            Color pillColor = isError ? Color.FromArgb(230, 196, 60, 58) : Color.FromArgb(230, 55, 158, 96);
            using var pillFont = new Font("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
            SizeF pillTextSize = g.MeasureString(pill, pillFont);
            float pillWidth = Math.Min(panelRect.Width - 36, pillTextSize.Width + 28);
            var pillRect = new RectangleF(panelRect.X + 18, panelRect.Bottom - 38, pillWidth, 24);
            using GraphicsPath pillPath = RoundedRect(pillRect, 12f);
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

        private void PushToScreen(byte alpha)
        {
            if (!IsHandleCreated)
            {
                return;
            }

            var size = new NativeMethods.Size32(CanvasWidth, CanvasHeight);
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
