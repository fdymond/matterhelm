using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Win32;

namespace HtpcMatterBridge.Ui;

/// <summary>
/// Runtime-drawn tray iconography (no .ico assets): a Fluent-inspired house
/// silhouette (the Google Home nod) with the Matter tri-node motif knocked
/// out of its body, plus a corner status dot carrying the bridge state.
/// The glyph is monochrome and theme-aware — near-white on a dark taskbar,
/// near-black on a light one — matching how Windows 11 system tray glyphs
/// (OneDrive, Teams, Defender) read. Everything is drawn proportionally to
/// the requested pixel size so 16 px tray reality and larger exports stay
/// crisp from the same geometry.
/// </summary>
public static class TrayIcons
{
    /// <summary>Status-dot fill per bridge state (same palette the tray previously used as full-circle icons).</summary>
    private static readonly Dictionary<BridgeState, Color> DotColors = new()
    {
        [BridgeState.Disabled] = Color.FromArgb(158, 158, 158),
        [BridgeState.Running] = Color.FromArgb(255, 179, 0),
        [BridgeState.Connected] = Color.FromArgb(46, 160, 67),
        [BridgeState.Faulted] = Color.FromArgb(229, 72, 77),
    };

    /// <summary>
    /// Builds the per-state tray icons at the system small-icon size, themed
    /// for the current taskbar. Caller owns (and disposes) the icons.
    /// Theme is sampled once here; a taskbar theme flip mid-session keeps the
    /// old tint until the next app start — acceptable for a tray glyph.
    /// </summary>
    public static Dictionary<BridgeState, Icon> CreateStateIcons()
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        bool dark = TaskbarIsDark();
        var icons = new Dictionary<BridgeState, Icon>();
        foreach (BridgeState state in DotColors.Keys)
        {
            using Bitmap bitmap = Render(state, size, dark);
            icons[state] = ToIcon(bitmap);
        }

        return icons;
    }

    /// <summary>
    /// The Windows taskbar is dark unless <c>SystemUsesLightTheme</c> says
    /// otherwise (missing value = classic dark taskbar).
    /// </summary>
    private static bool TaskbarIsDark()
    {
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme",
                0);
            return value is not int light || light == 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Renders one state's icon into a fresh 32bpp bitmap of the given square size.</summary>
    public static Bitmap Render(BridgeState state, int size, bool darkTaskbar)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        // Disabled reads as "present but off": dimmed glyph, muted dot.
        int glyphAlpha = state == BridgeState.Disabled ? 140 : 255;
        Color glyph = darkTaskbar
            ? Color.FromArgb(glyphAlpha, 245, 245, 245)
            : Color.FromArgb(glyphAlpha, 32, 32, 32);

        DrawHouseWithMatterKnockout(g, size, glyph);
        DrawStatusDot(g, size, DotColors[state], darkTaskbar);
        return bitmap;
    }

    /// <summary>
    /// Fills the rounded house silhouette, then punches the Matter tri-node
    /// motif out of its body (SourceCopy + transparent fill keeps the holes
    /// anti-aliased instead of region-jagged).
    /// </summary>
    private static void DrawHouseWithMatterKnockout(Graphics g, int size, Color glyph)
    {
        float s = size;
        using var house = new GraphicsPath();
        // Roof apex → right eave → right wall → floor → left wall → left eave.
        house.AddPolygon(
        [
            new PointF(0.50f * s, 0.05f * s),
            new PointF(0.95f * s, 0.44f * s),
            new PointF(0.83f * s, 0.44f * s),
            new PointF(0.83f * s, 0.90f * s),
            new PointF(0.17f * s, 0.90f * s),
            new PointF(0.17f * s, 0.44f * s),
            new PointF(0.05f * s, 0.44f * s),
        ]);

        using (var fill = new SolidBrush(glyph))
        {
            g.FillPath(fill, house);
            // Rounded joins: restroke the silhouette with a round-join pen so
            // the roof apex and corners lose their GDI+ needle points.
            using var soften = new Pen(glyph, Math.Max(1.4f, 0.09f * s))
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawPath(soften, house);
        }

        // Matter tri-node: a center node linked to three satellites (one up,
        // two down) — knocked out of the filled body so it shows the taskbar
        // through the glyph. Center sits up-left of the body's middle so the
        // motif never collides with the bottom-right status badge; node and
        // line sizes are floored in device pixels to stay legible at 16 px.
        var center = new PointF(0.46f * s, 0.60f * s);
        float arm = 0.145f * s;
        float nodeR = Math.Max(1.5f, 0.065f * s);
        float lineW = Math.Max(1.2f, 0.05f * s);

        CompositingMode previous = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var hole = new SolidBrush(Color.Transparent))
        using (var holePen = new Pen(Color.Transparent, lineW))
        {
            for (int i = 0; i < 3; i++)
            {
                double angle = (-90 + (i * 120)) * Math.PI / 180.0;
                var satellite = new PointF(
                    center.X + (float)(arm * Math.Cos(angle)),
                    center.Y + (float)(arm * Math.Sin(angle)));
                g.DrawLine(holePen, center, satellite);
                g.FillEllipse(hole, satellite.X - nodeR, satellite.Y - nodeR, 2 * nodeR, 2 * nodeR);
            }

            g.FillEllipse(hole, center.X - nodeR, center.Y - nodeR, 2 * nodeR, 2 * nodeR);
        }

        g.CompositingMode = previous;
    }

    /// <summary>
    /// Bottom-right status dot with a taskbar-toned separation ring — the
    /// Windows-11-badge way of carrying state without recoloring the glyph.
    /// </summary>
    private static void DrawStatusDot(Graphics g, int size, Color dot, bool darkTaskbar)
    {
        float s = size;
        float r = 0.21f * s;
        float cx = s - r - (0.03f * s);
        float cy = s - r - (0.03f * s);
        Color ring = darkTaskbar ? Color.FromArgb(32, 32, 32) : Color.FromArgb(238, 238, 238);

        // Ring first (slightly larger disc), then the colored dot on top; the
        // ring visually separates the badge from the glyph behind it.
        using var ringBrush = new SolidBrush(ring);
        float ringR = r + Math.Max(1.2f, 0.055f * s);
        g.FillEllipse(ringBrush, cx - ringR, cy - ringR, 2 * ringR, 2 * ringR);
        using var dotBrush = new SolidBrush(dot);
        g.FillEllipse(dotBrush, cx - r, cy - r, 2 * r, 2 * r);
    }

    /// <summary>Clones the bitmap into a GDI+-owned <see cref="Icon"/> and destroys the intermediate native handle.</summary>
    private static Icon ToIcon(Bitmap bitmap)
    {
        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using Icon borrowed = Icon.FromHandle(hIcon);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    /// <summary>
    /// Debug/design aid (<c>--export-tray-icons</c>): writes per-state PNGs at
    /// several sizes plus a contact sheet on dark and light taskbar strips.
    /// Returns the export directory.
    /// </summary>
    public static string ExportPreviews(string? directory = null)
    {
        string dir = directory ?? Path.Combine(AppContext.BaseDirectory, "tray-icons");
        Directory.CreateDirectory(dir);
        int[] sizes = [16, 24, 32, 48];
        BridgeState[] states =
            [BridgeState.Disabled, BridgeState.Running, BridgeState.Connected, BridgeState.Faulted];

        foreach (bool dark in (bool[])[true, false])
        {
            foreach (BridgeState state in states)
            {
                foreach (int size in sizes)
                {
                    using Bitmap bmp = Render(state, size, dark);
                    bmp.Save(
                        Path.Combine(dir, $"{state}-{size}px-{(dark ? "dark" : "light")}.png"),
                        ImageFormat.Png);
                }
            }
        }

        // Contact sheet: each theme gets a strip per size, states left→right,
        // drawn on the matching taskbar color with generous padding.
        const int pad = 12;
        int cell = 48 + pad;
        int sheetW = (states.Length * cell) + pad;
        int stripH = sizes.Sum(sz => sz + pad) + pad;
        using var sheet = new Bitmap(sheetW * 2, stripH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(sheet))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int t = 0; t < 2; t++)
            {
                bool dark = t == 0;
                using var back = new SolidBrush(
                    dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(238, 238, 238));
                g.FillRectangle(back, t * sheetW, 0, sheetW, stripH);
                int y = pad;
                foreach (int size in sizes)
                {
                    for (int i = 0; i < states.Length; i++)
                    {
                        using Bitmap bmp = Render(states[i], size, dark);
                        g.DrawImageUnscaled(bmp, (t * sheetW) + pad + (i * cell), y);
                    }

                    y += size + pad;
                }
            }
        }

        sheet.Save(Path.Combine(dir, "contact-sheet.png"), ImageFormat.Png);
        return dir;
    }
}
